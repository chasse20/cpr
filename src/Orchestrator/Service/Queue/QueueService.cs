using CPR.Service.GitHub;
using CPR.Service.Review;
using CPR.Service.SSH;
using CPR.SQL;
using System.Text.Json.Nodes;

namespace CPR.Service.Queue
{
	public sealed class QueueService( IServiceScopeFactory tScopes, QueueGate tGate, GitHubClient tGitHub, WorkerClient tWorkers, GitPublisher tPublisher, CleanupService tCleanup, ReviewHistoryService tHistory, IOptions<Settings> tSettings, ILogger<QueueService> tLogger ) : BackgroundService
	{
		private readonly Settings _settings = tSettings.Value;
		private DateTimeOffset? _nextRecoveryAt;

		protected override async Task ExecuteAsync( CancellationToken tCancel )
		{
			await Task.Delay( TimeSpan.FromSeconds( 2 ), tCancel );
			bool tempIsRecovered = false;
			DateTimeOffset tempNextPoll = DateTimeOffset.MinValue;

			while ( !tCancel.IsCancellationRequested )
			{
				try
				{
					if ( DateTimeOffset.UtcNow >= tempNextPoll )
					{
						if ( !tempIsRecovered )
						{
							await RecoverInterruptedAsync( tCancel );
							tempIsRecovered = true;
						}

						await CheckWorkersAsync( tCancel );
						await PollGitHubAsync( tCancel );
						await tCleanup.ScheduleAsync( tCancel );
						await ParkWaitingWorkersAsync( tCancel );
						tempNextPoll = DateTimeOffset.UtcNow.AddSeconds( Math.Max( 15, _settings.PollSeconds ) );
					}

					await ScheduleAsync( tCancel );
				}
				catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
				{
					break;
				}
				catch ( Exception tException )
				{
					tLogger.LogError( tException, "Orchestration poll failed; will retry" );
					tempNextPoll = DateTimeOffset.UtcNow.AddSeconds( Math.Max( 15, _settings.PollSeconds ) );
				}

				DateTimeOffset tempWakeAt = _nextRecoveryAt is not null && _nextRecoveryAt < tempNextPoll ? _nextRecoveryAt.Value : tempNextPoll;
				await tGate.WaitForSchedulingAsync( tempWakeAt - DateTimeOffset.UtcNow, tCancel );
			}
		}

		public override async Task StopAsync( CancellationToken tCancel )
		{
			try
			{
				await base.StopAsync( tCancel );
			}
			finally
			{
				await tGate.DrainAsync( tCancel );
			}
		}

		private async Task RecoverInterruptedAsync( CancellationToken tCancel )
		{
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				List<PullRequest> tempInterrupted = await tempContext.PullRequest.Where( x => x.Status == PullRequestStatus.Processing || ( x.Status == PullRequestStatus.ReadyForApproval && x.ReviewedTargetSha == null ) ).ToListAsync( tCancel );

				foreach ( PullRequest tempRequest in tempInterrupted.Where( x => !tGate.IsRunning( x.PullRequestId ) ) )
				{
					ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );

					if ( tempRequest.Status == PullRequestStatus.Processing && tempRequest.WorkerId is not null && tempRequest.ApprovedSha is null && tempRequest.WorkReason is PullRequestWorkReason.ManualTweak or PullRequestWorkReason.Interactive or PullRequestWorkReason.CiRecovery )
					{
						// The interactive SSH session survives an orchestrator restart. Leave
						// its worker assigned so /complete can capture the saved checkout.
						tempRecovery.Action = "Manual session retained across orchestrator restart";
						tempRecovery.Save( tempRequest );
						tempRequest.LastError = tempRecovery.Action;
					}
					else if ( tempRequest.Status == PullRequestStatus.Processing && tempRecovery.OperationId is not null && tempRequest.WorkerId is not null && tempRequest.ApprovedSha is null )
					{
						tempRecovery.IsPending = true;
						tempRecovery.Category = "interrupted";
						tempRecovery.Action = "Checking the worker before resuming interrupted work";
						tempRecovery.NextAttemptAt = DateTimeOffset.UtcNow;
						tempRecovery.Save( tempRequest );
						tempRequest.LastError = tempRecovery.Action;
					}
					else
					{
						WorkflowRules.Block( tempRequest, "Interrupted manual, approved, or legacy work; use connect to reconcile the saved workspace", "interrupted" );
					}
				}
				List<PullRequest> tempBlocked = await tempContext.PullRequest.Where( x => x.Status == PullRequestStatus.Blocked ).ToListAsync( tCancel );
				foreach ( PullRequest tempRequest in tempBlocked )
				{
				string? tempCategory = ReviewRecovery.Read( tempRequest ).Category;
				if ( tempCategory is "maintainer_decision" or "not_needed" )
				{
					tempRequest.Status = tempCategory == "not_needed" ? PullRequestStatus.NoLongerNeeded : PullRequestStatus.AwaitingDecision;
				}
				}

				await tempContext.SaveChangesAsync( tCancel );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}

		private async Task CheckWorkersAsync( CancellationToken tCancel )
		{
			if ( !_settings.IsWorkerExecutionEnabled )
			{
				return;
			}

			using IServiceScope tempScope = tScopes.CreateScope();
			Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
			List<Worker> tempWorkers = await tempContext.Worker.AsNoTracking().ToListAsync( tCancel );

			(int WorkerId, bool IsHealthy)[] tempHealth = await Task.WhenAll( tempWorkers.Where( x => x.Status != WorkerStatus.Paused ).Select( async x => (x.WorkerId, await tWorkers.IsHealthyAsync( x, tCancel )) ) );
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				foreach ( (int tempId, bool tempIsHealthy) in tempHealth )
				{
					Worker? tempWorker = await tempContext.Worker.FindAsync( [ tempId ], tCancel );

					if ( tempWorker is not null && tempWorker.Status != WorkerStatus.Paused )
					{
						tempWorker.Status = tempIsHealthy ? WorkerStatus.Online : WorkerStatus.Offline;

						if ( tempIsHealthy )
						{
							tempWorker.LastSeenAt = DateTimeOffset.UtcNow;
						}
					}
				}

				await tempContext.SaveChangesAsync( tCancel );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}

		private async Task PollGitHubAsync( CancellationToken tCancel )
		{
			using IServiceScope tempScope = tScopes.CreateScope();
			Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
			List<PullRequest> tempRequests = await tempContext.PullRequest.AsNoTracking().Where( x => x.Status != PullRequestStatus.Merged && x.Status != PullRequestStatus.Closed && x.Status != PullRequestStatus.Removed ).ToListAsync( tCancel );

			foreach ( PullRequest tempRequest in tempRequests )
			{
				await PollPullRequestAsync( tempContext, tempRequest.PullRequestId, $"https://github.com/{tempRequest.Repository}/pull/{tempRequest.Number}", tCancel );
			}
		}

		private async Task PollPullRequestAsync( Context tContext, long tId, string tUrl, CancellationToken tCancel )
		{
			PullRequest? tempObserved = await tContext.PullRequest.AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );
			GitHubPullRequest tempSnapshot;

			try
			{
				tempSnapshot = await tGitHub.ReadAsync( tUrl, tCancel );
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				tLogger.LogWarning( "Unable to poll PR {Id}: {Reason}", tId, tException.Message );

				return;
			}

			// Follow later commits after approval without changing the recorded publication
			CIResult tempChecks = CIResult.Pending;
			string? tempCIError = null;

			if ( tempObserved is not null && WorkflowRules.IsFollowingCI( tempObserved ) && WorkflowRules.IsSameBranch( tempObserved, tempSnapshot ) )
			{
				try
				{
					tempChecks = await tGitHub.ReadCIAsync( tUrl, tempSnapshot, _settings.IsCICheckRequired, tCancel );
				}
				catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
				{
					tLogger.LogWarning( "CI unavailable for PR {Id}: {Reason}", tId, tException.Message );
					tempCIError = $"Unable to read CI; polling will retry: {tException.Message}";
				}
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				tContext.ChangeTracker.Clear();
				PullRequest? tempRequest = await tContext.PullRequest.FindAsync( [ tId ], tCancel );

				if ( tempRequest is null || tempRequest.Status is PullRequestStatus.Merged or PullRequestStatus.Closed or PullRequestStatus.Removed )
				{
					return;
				}

				if ( tempSnapshot.State is "MERGED" or "CLOSED" )
				{
					tempRequest.Status = tempSnapshot.State == "MERGED" ? PullRequestStatus.Merged : PullRequestStatus.Closed;
					tempRequest.CompletedAt = tempSnapshot.CompletedAt ?? DateTimeOffset.UtcNow;
					tempRequest.RemoteSha = tempSnapshot.HeadSha;
					tempRequest.CleanedAt = null;
					tempRequest.CleanupRequestedAt = DateTimeOffset.UtcNow;
					tempRequest.ReviewedTargetSha = null;
					tempRequest.PreparedSha = null;
					tempRequest.PreparedReviewRunId = null;
					tempRequest.ApprovedSha = null;
					tempRequest.LastError = "File cleanup pending";
					tempRequest.ReviewFeedback = "{}";
					await tContext.SaveChangesAsync( tCancel );

					return;
				}

				if ( tempSnapshot.State != "OPEN" )
				{
					tLogger.LogWarning( "Unrecognized GitHub state for PR {Id}: {State}", tId, tempSnapshot.State );

					return;
				}

				// Discard polls overtaken by queue actions or a publication in progress
				if ( tempObserved is null || tempRequest.Status != tempObserved.Status || tempRequest.RemoteSha != tempObserved.RemoteSha || tempRequest.TargetBranch != tempObserved.TargetBranch || tempRequest.PublishedSha != tempObserved.PublishedSha || ( tempRequest.ApprovedSha is not null && tGate.IsRunning( tId ) ) )
				{
					return;
				}

				// Cancellation remains stopped when a new source revision arrives
				if ( tempRequest.Status == PullRequestStatus.Blocked && ReviewRecovery.Read( tempRequest ).Category == "cancelled" )
				{
					return;
				}

				if ( tempRequest.Status == PullRequestStatus.WaitingToBeProcessed && tempRequest.WorkerId is null && tempRequest.WorkspaceWorkerId is null )
				{
					tempRequest.RemoteSha = tempSnapshot.HeadSha;
					tempRequest.TargetBranch = tempSnapshot.TargetBranch;
					tempRequest.HeadBranch = tempSnapshot.HeadBranch;
					tempRequest.HeadRepository = tempSnapshot.HeadRepository;
				}
				else if ( WorkflowRules.IsFollowingCI( tempRequest ) && WorkflowRules.IsSameBranch( tempRequest, tempSnapshot ) )
				{
					tempRequest.RemoteSha = tempSnapshot.HeadSha;
				}
				else if ( !WorkflowRules.IsSameSource( tempRequest, tempSnapshot ) )
				{
					ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );
					if ( tempRequest.Status == PullRequestStatus.Blocked && tempRequest.WorkerId is null && tempRequest.WorkspaceWorkerId is not null && tempRequest.ApprovedSha is null && tempRequest.PublishedSha is null && tempRecovery.Attempts < 2 && !tGate.IsRunning( tId ) && WorkflowRules.IsSameBranch( tempRequest, tempSnapshot ) )
					{
						// A new source revision needs a fresh review. Workspace.PrepareAsync archives
						// the old checkout before fetching it, retaining unpublished work.
						tempRequest.RemoteSha = tempSnapshot.HeadSha;
						tempRequest.WorkReason = PullRequestWorkReason.Rereview;
						tempRequest.Status = PullRequestStatus.WaitingToBeProcessed;
						tempRecovery.Attempts++;
						tempRecovery.IsPending = false;
						tempRecovery.IsContinuation = false;
						tempRecovery.OperationId = null;
						tempRecovery.Category = "snapshot";
						tempRecovery.Action = "Source head changed; reassessing the new revision automatically";
						tempRecovery.NextAttemptAt = null;
						tempRecovery.Save( tempRequest );
						tempRequest.LastError = tempRecovery.Action;
					}
					else
					{
						WorkflowRules.Block( tempRequest, "PR head or destination changed since review; use cpr review to reassess" );
					}
				}
				else if ( tempRequest.Status == PullRequestStatus.Blocked && tempRequest.WorkerId is null && tempRequest.WorkspaceWorkerId is not null && tempRequest.ApprovedSha is null && !tGate.IsRunning( tId ) )
				{
					ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );
					if ( tempRecovery.Category == "report_metadata" && tempRecovery.Attempts < 2 )
					{
						tempRequest.WorkReason = PullRequestWorkReason.ReportRetry;
						tempRequest.Status = PullRequestStatus.WaitingToBeProcessed;
						tempRecovery.Attempts++;
						tempRecovery.IsPending = false;
						tempRecovery.IsContinuation = false;
						tempRecovery.OperationId = null;
						tempRecovery.Action = "Retrying saved report and completion gates automatically";
						tempRecovery.NextAttemptAt = null;
						tempRecovery.Save( tempRequest );
						tempRequest.LastError = tempRecovery.Action;
					}
				}

				if ( WorkflowRules.IsFollowingCI( tempRequest ) )
				{
					if ( tempCIError is not null )
					{
						tempRequest.Status = PullRequestStatus.WaitingForCi;
						tempRequest.LastError = tempCIError[ ..Math.Min( 4000, tempCIError.Length ) ];
					}
					else
					{
						long tempFrontId = await tContext.PullRequest
							.Where( x => x.Repository == tempRequest.Repository && x.TargetBranch == tempRequest.TargetBranch && x.PublishedSha != null && ( x.Status == PullRequestStatus.WaitingForCi || x.Status == PullRequestStatus.WaitingForMergeTurn || x.Status == PullRequestStatus.ReadyToBeMerged || x.PullRequestId == tId ) )
							.OrderBy( x => x.PullRequestId ).Select( x => x.PullRequestId ).FirstAsync( tCancel );
						WorkflowDecision tempDecision = WorkflowRules.Decide( tempRequest, tempFrontId == tempRequest.PullRequestId, tempSnapshot, tempChecks );

						if ( tempRequest.Status != tempDecision.Status && tempDecision.Status == PullRequestStatus.WaitingToBeProcessed )
						{
							tempRequest.Recovery = "{}";
						}

						tempRequest.Status = tempDecision.Status;
						tempRequest.WorkReason = tempDecision.WorkReason;
						tempRequest.LastError = tempDecision.Status == PullRequestStatus.CiFailure ? "CI failed; checks will be polled again. Use review or connect for code repairs" : null;
					}
				}

				await tContext.SaveChangesAsync( tCancel );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}

		private async Task ParkWaitingWorkersAsync( CancellationToken tCancel )
		{
			if ( !_settings.IsWorkerExecutionEnabled )
			{
				return;
			}

			using IServiceScope tempScope = tScopes.CreateScope();
			Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
			PullRequestStatus[] tempStatuses = [ PullRequestStatus.ReadyForApproval, PullRequestStatus.Blocked, PullRequestStatus.AwaitingDecision, PullRequestStatus.NoLongerNeeded, PullRequestStatus.Removed, PullRequestStatus.WaitingForCi, PullRequestStatus.CiFailure, PullRequestStatus.WaitingForMergeTurn, PullRequestStatus.ReadyToBeMerged ];
			List<long> tempIds = await tempContext.PullRequest.AsNoTracking().Where( x => tempStatuses.Contains( x.Status ) && x.CleanupRequestedAt == null && x.ApprovedSha == null && x.WorkerId != null ).Select( x => x.PullRequestId ).ToListAsync( tCancel );

			foreach ( long tempId in tempIds )
			{
				PullRequest? tempRequest;
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					tempRequest = await tempContext.PullRequest.Include( x => x.Worker ).AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tempId, tCancel );

					if ( tempRequest?.Worker is null || !tempStatuses.Contains( tempRequest.Status ) || tempRequest.CleanupRequestedAt is not null || tempRequest.ApprovedSha is not null || !tGate.TryBegin( tempId ) )
					{
						tempRequest = null;
					}
				}
				finally
				{
					tGate.Mutex.Release();
				}

				if ( tempRequest?.Worker is not null )
				{
					bool tempIsPublished = tempRequest.Status is PullRequestStatus.WaitingForCi or PullRequestStatus.CiFailure or PullRequestStatus.WaitingForMergeTurn or PullRequestStatus.ReadyToBeMerged;
					bool tempIsTransfer = tempRequest.WorkspaceWorkerId is not null && tempRequest.WorkspaceWorkerId != tempRequest.WorkerId;

					try
					{
						if ( !tempIsTransfer )
						{
							await tHistory.CaptureAsync( tempRequest.Worker, tempRequest, tCancel );
						}

						if ( tempIsTransfer )
						{
							await tWorkers.DetachAsync( tempRequest.Worker, tempRequest, tCancel );
						}
						else if ( tempIsPublished )
						{
							await tWorkers.ReleaseAsync( tempRequest.Worker, tempRequest, tCancel );
						}
						else if ( tempRequest.Status == PullRequestStatus.ReadyForApproval )
						{
							await tWorkers.ParkAsync( tempRequest.Worker, tempRequest, tCancel );
						}
						else
						{
							await tWorkers.DetachAsync( tempRequest.Worker, tempRequest, tCancel );
						}

						await tGate.Mutex.WaitAsync( tCancel );

						try
						{
							PullRequest? tempCurrent = await tempContext.PullRequest.FindAsync( [ tempId ], tCancel );

							if ( tempCurrent is not null && tempCurrent.WorkerId == tempRequest.WorkerId && tempStatuses.Contains( tempCurrent.Status ) && tempCurrent.ApprovedSha is null )
							{
								tempCurrent.WorkspaceWorkerId = tempIsTransfer ? tempRequest.WorkspaceWorkerId : tempIsPublished ? null : tempRequest.WorkerId;
								tempCurrent.WorkerId = null;
								await tempContext.SaveChangesAsync( tCancel );
							}
						}
						finally
						{
							tGate.Mutex.Release();
						}
					}
					catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
					{
						tLogger.LogWarning( "Unable to park PR {Id}: {Reason}", tempId, tException.Message );
					}
					finally
					{
						tGate.Complete( tempId );
						tempContext.ChangeTracker.Clear();
					}
				}
			}
		}

		private async Task ScheduleAsync( CancellationToken tCancel )
		{
			if ( !_settings.IsWorkerExecutionEnabled )
			{
				return;
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				List<Worker> tempFree = await tempContext.Worker.Where( x => x.Status == WorkerStatus.Online && x.PullRequest == null ).OrderBy( x => x.WorkerId ).ToListAsync( tCancel );
				HashSet<int> tempFreeIds = [ .. tempFree.Where( x => !tGate.CleaningWorkers.Contains( x.WorkerId ) ).Select( x => x.WorkerId ) ];
				List<int> tempOnlineIds = await tempContext.Worker.Where( x => x.Status == WorkerStatus.Online ).Select( x => x.WorkerId ).ToListAsync( tCancel );
				HashSet<int> tempTransferIds = [ .. tempOnlineIds.Where( x => !tGate.CleaningWorkers.Contains( x ) ) ];
				List<PullRequest> tempJobs = await tempContext.PullRequest
					.Where( x => x.WorkerId == null && ( x.Status == PullRequestStatus.WaitingToBeProcessed || ( _settings.IsPublicationEnabled && x.Status == PullRequestStatus.ReadyForApproval && x.ApprovedSha != null ) ) )
					.OrderBy( x => x.ApprovedSha != null ? 0 : x.WorkReason == PullRequestWorkReason.ManualTweak || x.WorkReason == PullRequestWorkReason.Interactive || x.WorkReason == PullRequestWorkReason.CiRecovery || x.WorkReason == PullRequestWorkReason.Rereview || x.WorkReason == PullRequestWorkReason.ReportRetry || x.WorkReason == PullRequestWorkReason.FeedbackRepair ? 1 : x.WorkReason == PullRequestWorkReason.MergeConflict ? 2 : 3 )
					.ThenBy( x => x.PullRequestId ).ToListAsync( tCancel );
				List<PullRequest> tempReviews = [];
				List<PullRequest> tempProcessing = await tempContext.PullRequest.Where( x => x.Status == PullRequestStatus.Processing && x.WorkerId != null ).ToListAsync( tCancel );
				_nextRecoveryAt = null;

				foreach ( PullRequest tempRequest in tempProcessing )
				{
					ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );

					if ( tempRecovery.IsPending && !tGate.IsRunning( tempRequest.PullRequestId ) )
					{
						DateTimeOffset tempDue = tempRecovery.NextAttemptAt ?? DateTimeOffset.UtcNow;

						if ( tempDue <= DateTimeOffset.UtcNow )
						{
							tempReviews.Add( tempRequest );
						}
						else if ( _nextRecoveryAt is null || tempDue < _nextRecoveryAt )
						{
							_nextRecoveryAt = tempDue;
						}
					}
				}

				foreach ( Worker tempWorker in tempFree.Where( x => !tGate.CleaningWorkers.Contains( x.WorkerId ) ) )
				{
					PullRequest? tempRequest = tempJobs.FirstOrDefault( x => x.WorkerId is null && !tGate.IsRunning( x.PullRequestId ) && ( x.WorkspaceWorkerId is null || x.WorkspaceWorkerId == tempWorker.WorkerId || x.ApprovedSha is not null && !tempFreeIds.Contains( x.WorkspaceWorkerId.Value ) && tempTransferIds.Contains( x.WorkspaceWorkerId.Value ) ) );

					if ( tempRequest is not null )
					{
						if ( tempRequest.WorkspaceWorkerId is null )
						{
							tempRequest.SessionId = null;
						}

						tempRequest.StorageWorkerIds = [ .. tempRequest.StorageWorkerIds.Append( tempWorker.WorkerId ).Distinct() ];
						tempRequest.WorkerId = tempWorker.WorkerId;
						tempRequest.WorkspaceWorkerId ??= tempWorker.WorkerId;
						tempRequest.LastError = null;
						tempFreeIds.Remove( tempWorker.WorkerId );

						if ( tempRequest.Status == PullRequestStatus.WaitingToBeProcessed )
						{
							ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );
							tempRecovery.OperationId ??= Guid.NewGuid().ToString( "N" );
							tempRecovery.Save( tempRequest );
							tempRequest.Status = PullRequestStatus.Processing;
							tempRequest.PreparedSha = null;
							tempRequest.PreparedReviewRunId = null;
							tempRequest.ReviewedTargetSha = null;
							tempRequest.ApprovedSha = null;
							tempReviews.Add( tempRequest );
						}
					}
				}

				await tempContext.SaveChangesAsync( tCancel );

				foreach ( PullRequest tempRequest in tempReviews )
				{
					long tempId = tempRequest.PullRequestId;
					int tempWorkerId = tempRequest.WorkerId!.Value;

					tGate.TryRun( tempId, tJobCancel => ProcessJobAsync( tempId, tempWorkerId, tJobCancel ) );
				}

				if ( _settings.IsPublicationEnabled )
				{
					List<long> tempApprovals = await tempContext.PullRequest.AsNoTracking()
						.Where( x => x.Status == PullRequestStatus.ReadyForApproval && x.ApprovedSha != null && x.WorkerId != null )
						.Select( x => x.PullRequestId ).ToListAsync( tCancel );

					foreach ( long tempId in tempApprovals )
					{
						tGate.TryRun( tempId, tJobCancel => PublishJobAsync( tempId, tJobCancel ) );
					}
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}

		private async Task ProcessJobAsync( long tId, int tWorkerId, CancellationToken tCancel )
		{
			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				PullRequest tempRequest = await tempContext.PullRequest.AsNoTracking().SingleAsync( x => x.PullRequestId == tId, tCancel );
				Worker tempWorker = await tempContext.Worker.AsNoTracking().SingleAsync( x => x.WorkerId == tWorkerId, tCancel );

				if ( tempRequest.Status != PullRequestStatus.Processing || tempRequest.WorkerId != tWorkerId )
				{
					return;
				}

				ReviewService tempReview = tempScope.ServiceProvider.GetRequiredService<ReviewService>();

				if ( tempRequest.WorkReason is PullRequestWorkReason.CiRecovery or PullRequestWorkReason.Interactive or PullRequestWorkReason.ManualTweak )
				{
					// Interactive work starts only after cpr connect
					await tempReview.PrepareInteractiveAsync( tempWorker, tempRequest, tCancel );
					tLogger.LogInformation( "PR {Id} prepared for interactive work on worker {Worker}", tId, tWorkerId );

					return;
				}

				ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );
				WorkerSnapshot? tempResult = null;

				if ( tempRecovery.IsPending )
				{
					WorkerReviewState tempState;

					try
					{
						tempState = await tWorkers.InspectReviewAsync( tempWorker, tempRequest, tempRecovery.OperationId!, tCancel );
					}
					catch ( Exception ) when ( !tCancel.IsCancellationRequested )
					{
						++tempRecovery.Attempts;
						await SaveRecoveryAsync( tId, tempRecovery, tCancel );

						throw;
					}

					if ( tempState.Status == "running" )
					{
						tempRecovery.Action = tempState.Action;
						tempRecovery.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds( 30 );
						await SaveRecoveryAsync( tId, tempRecovery, tCancel );

						return;
					}

					if ( tempState.Status == "failed" && tempState.Category is not ( "transient" or "interrupted" ) && !( tempRecovery.IsContinuation && tempState.Category is "required_check" or "unfinished_review" or "missing_evidence" ) )
					{
						throw new ReviewFailureException( tempState.Category ?? "infrastructure", tempState.Action ?? "Inspect the saved worker failure" );
					}

					tempResult = tempState.Snapshot;

					if ( tempResult is not null )
					{
						GitHubPullRequest tempRemote = await tGitHub.ReadAsync( $"https://github.com/{tempRequest.Repository}/pull/{tempRequest.Number}", tCancel );

						if ( !WorkflowRules.IsSameSource( tempRequest, tempRemote ) || tempResult.IsHistorical )
						{
							throw new ReviewFailureException( "snapshot", "Completed worker result no longer matches the PR source; use review to reassess" );
						}

						await tHistory.CaptureAsync( tempWorker, tempRequest, tCancel );
					}
					else
					{
						if ( tempRecovery.Attempts >= 2 )
						{
							throw new ReviewFailureException( "exhausted", "Automatic recovery exhausted two retries; use connect to inspect the retained work" );
						}

						++tempRecovery.Attempts;
						tempRecovery.IsPending = false;
						tempRecovery.IsContinuation = tempRecovery.IsContinuation || tempState.Status == "failed" || tempState.Category == "interrupted";
						tempRecovery.NextAttemptAt = null;
						tempRecovery.Action = "Resuming missing stages and checks affected by interrupted edits";
						await SaveRecoveryAsync( tId, tempRecovery, tCancel );
						tempRecovery.Save( tempRequest );
					}
				}

				tempResult ??= await tempReview.ExecuteAsync( tempWorker, tempRequest, tCancel );
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest? tempCurrent = await tempContext.PullRequest.FindAsync( [ tId ], tCancel );

					if ( tempCurrent is not null && tempCurrent.WorkerId == tWorkerId && tempCurrent.Status == PullRequestStatus.Processing )
					{
						tempCurrent.PreparedReviewRunId = await ReviewComment.PrepareAsync( tempContext, tempCurrent, tempResult, tCancel );
						tempCurrent.PreparedSha = tempResult.TreeSha;
						tempCurrent.ReviewedTargetSha = tempResult.TargetSha;
						tempCurrent.SessionId = tempResult.SessionId ?? tempCurrent.SessionId;
						tempCurrent.WorkspaceWorkerId = tWorkerId;
						tempCurrent.WorkerId = null;
						tempRecovery.IsPending = false;
						tempRecovery.NextAttemptAt = null;
						tempRecovery.Action = "Review complete; inspect and approve to publish";
						tempRecovery.Save( tempCurrent );
						tempCurrent.Status = PullRequestStatus.ReadyForApproval;
						tempCurrent.LastError = null;
						await tempContext.SaveChangesAsync( tCancel );
					}
				}
				finally
				{
					tGate.Mutex.Release();
				}
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				tLogger.LogError( tException, "Codex job {Id} stopped; saved work retained", tId );
				await RecordFailureAsync( tId, tException, false, tCancel );
			}
		}

		private async Task PublishJobAsync( long tId, CancellationToken tCancel )
		{
			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				PullRequest tempRequest = await tempContext.PullRequest.AsNoTracking().SingleAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempRequest.ApprovedSha is null || tempRequest.WorkerId is null || tempRequest.Status != PullRequestStatus.ReadyForApproval )
				{
					return;
				}

				Worker tempWorker = await tempContext.Worker.AsNoTracking().SingleAsync( x => x.WorkerId == tempRequest.WorkerId.Value, tCancel );
				ReviewRun tempRun = await ReviewComment.GetPreparedAsync( tempContext, tempRequest, tCancel );
				GitHubPullRequest tempRemote = await tGitHub.ReadAsync( $"https://github.com/{tempRequest.Repository}/pull/{tempRequest.Number}", tCancel );

				if ( !WorkflowRules.IsSameBranch( tempRequest, tempRemote ) )
				{
					throw new InvalidOperationException( "PR source branch or destination changed; request a fresh review before publication" );
				}

				if ( tempRequest.WorkspaceWorkerId != tempWorker.WorkerId )
				{
					Worker tempSource = await tempContext.Worker.AsNoTracking().SingleAsync( x => x.WorkerId == tempRequest.WorkspaceWorkerId, tCancel );
					await tWorkers.MoveApprovedAsync( tempSource, tempWorker, tempRequest, tCancel );
					await tGate.Mutex.WaitAsync( tCancel );

					try
					{
						PullRequest tempCurrent = await tempContext.PullRequest.SingleAsync( x => x.PullRequestId == tId, tCancel );
						if ( tempCurrent.Status != PullRequestStatus.ReadyForApproval || tempCurrent.ApprovedSha != tempRequest.ApprovedSha || tempCurrent.PreparedReviewRunId != tempRequest.PreparedReviewRunId || tempCurrent.WorkerId != tempWorker.WorkerId || tempCurrent.WorkspaceWorkerId != tempSource.WorkerId || tempCurrent.RemoteSha != tempRequest.RemoteSha || tempCurrent.MaintainerResolution != tempRequest.MaintainerResolution )
						{
							throw new InvalidOperationException( "Approval or workspace ownership changed during transfer; both checkouts retained" );
						}

						// Switch ownership only after the destination confirms the approved commit
						tempCurrent.WorkspaceWorkerId = tempWorker.WorkerId;
						tempCurrent.SessionId = null;
						await tempContext.SaveChangesAsync( tCancel );
						tempRequest = tempCurrent;
						tempContext.Entry( tempCurrent ).State = EntityState.Detached;
					}
					finally
					{
						tGate.Mutex.Release();
					}
				}

				// Retain approved repairs and finish the pending merge before newer integration
				string tempCommit = await tWorkers.CommitApprovedAsync( tempWorker, tempRequest, tCancel );

				if ( !WorkflowRules.IsSameSource( tempRequest, tempRemote ) && tempRemote.HeadSha != tempCommit )
				{
					throw new InvalidOperationException( "PR head changed; preserve the saved work and request a fresh review" );
				}

				if ( tempRemote.TargetSha != tempRequest.ReviewedTargetSha && WorkflowRules.IsSameSource( tempRequest, tempRemote ) )
				{
					await tGate.Mutex.WaitAsync( tCancel );

					try
					{
						PullRequest tempCurrent = await tempContext.PullRequest.SingleAsync( x => x.PullRequestId == tId, tCancel );

						if ( tempCurrent.Status != PullRequestStatus.ReadyForApproval || tempCurrent.ApprovedSha != tempRequest.ApprovedSha || tempCurrent.PreparedReviewRunId != tempRequest.PreparedReviewRunId || tempCurrent.WorkerId != tempWorker.WorkerId )
						{
							throw new InvalidOperationException( "Approval changed before target integration" );
						}

						tempCurrent.Status = PullRequestStatus.Processing;
						tempCurrent.WorkReason = PullRequestWorkReason.MergeConflict;
						tempCurrent.Recovery = "{}";
						tempRequest.Recovery = "{}";
						tempCurrent.LastError = "Approved target integration in progress; publication follows passing checks";
						await tempContext.SaveChangesAsync( tCancel );
						tempRequest.WorkReason = tempCurrent.WorkReason;
					}
					finally
					{
						tGate.Mutex.Release();
					}

					ReviewService tempReview = tempScope.ServiceProvider.GetRequiredService<ReviewService>();
					WorkerSnapshot tempResult = await tempReview.ExecuteAsync( tempWorker, tempRequest, tCancel );
					await tGate.Mutex.WaitAsync( tCancel );

					try
					{
						tempContext.ChangeTracker.Clear();
						PullRequest tempCurrent = await tempContext.PullRequest.SingleAsync( x => x.PullRequestId == tId, tCancel );

						if ( tempCurrent.Status != PullRequestStatus.Processing || tempCurrent.ApprovedSha != tempRequest.ApprovedSha || tempCurrent.PreparedReviewRunId != tempRequest.PreparedReviewRunId || tempCurrent.WorkerId != tempWorker.WorkerId || tempCurrent.RemoteSha != tempRequest.RemoteSha || tempCurrent.MaintainerResolution != tempRequest.MaintainerResolution )
						{
							throw new InvalidOperationException( "Approval changed during target integration; saved work retained" );
						}

						tempCurrent.PreparedReviewRunId = await ReviewComment.PrepareAsync( tempContext, tempCurrent, tempResult, tCancel );
						tempCurrent.PreparedSha = tempResult.TreeSha;
						tempCurrent.ApprovedSha = tempResult.TreeSha;
						tempCurrent.ReviewedTargetSha = tempResult.TargetSha;
						tempCurrent.SessionId = tempResult.SessionId ?? tempCurrent.SessionId;
						tempCurrent.Status = PullRequestStatus.ReadyForApproval;
						tempCurrent.LastError = null;
						await tempContext.SaveChangesAsync( tCancel );
						tempRequest = tempCurrent;
						tempContext.Entry( tempCurrent ).State = EntityState.Detached;
						tempRun = await ReviewComment.GetPreparedAsync( tempContext, tempRequest, tCancel );
					}
					finally
					{
						tGate.Mutex.Release();
					}

					tempCommit = await tWorkers.CommitApprovedAsync( tempWorker, tempRequest, tCancel );
				}

				await tPublisher.PushAsync( tempRequest, tempWorker, tempCommit, tCancel );
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest? tempCurrent = await tempContext.PullRequest.FindAsync( [ tId ], tCancel );

					if ( tempCurrent is not null && tempCurrent.Status == PullRequestStatus.ReadyForApproval && tempCurrent.ApprovedSha == tempRequest.ApprovedSha && tempCurrent.PreparedReviewRunId == tempRequest.PreparedReviewRunId && tempCurrent.WorkerId == tempRequest.WorkerId )
					{
						// A repeated approval of the same commit does not create another comment
						if ( !await tempContext.ReviewRun.AnyAsync( x => x.PullRequestId == tId && x.PublishedSha == tempCommit, tCancel ) )
						{
							tempRun.PublishedSha = tempCommit;
						}

						// Consume pending updates even when an empty session kept the existing commit
						JsonObject tempDetails = JsonNode.Parse( tempRun.Details )!.AsObject();
						tempDetails[ "publication" ] ??= new JsonObject();
						tempDetails[ "publication" ]![ "published_sha" ] = tempCommit;
						tempRun.Details = tempDetails.ToJsonString();

						tempCurrent.PublishedSha = tempCommit;
						tempCurrent.ReviewedTargetSha = null;
						tempCurrent.RemoteSha = tempCommit;
						tempCurrent.PreparedSha = null;
						tempCurrent.PreparedReviewRunId = null;
						tempCurrent.ApprovedSha = null;

						// Keep the worker reserved until release succeeds; polling retries release
						tempCurrent.WorkspaceWorkerId = tempRequest.WorkerId;
						tempCurrent.SessionId = null;
						tempCurrent.Status = PullRequestStatus.WaitingForCi;
						tempCurrent.LastError = null;
						await tempContext.SaveChangesAsync( tCancel );
					}
				}
				finally
				{
					tGate.Mutex.Release();
				}

				await tWorkers.ReleaseAsync( tempWorker, tempRequest, tCancel );
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					tempContext.ChangeTracker.Clear();
					PullRequest? tempCurrent = await tempContext.PullRequest.FindAsync( [ tId ], tCancel );

					if ( tempCurrent is not null && tempCurrent.WorkerId == tempWorker.WorkerId && tempCurrent.PublishedSha == tempCommit )
					{
						tempCurrent.WorkerId = null;
						tempCurrent.WorkspaceWorkerId = null;
						await tempContext.SaveChangesAsync( tCancel );
					}
				}
				finally
				{
					tGate.Mutex.Release();
				}
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				tLogger.LogError( tException, "Approved publication {Id} stopped; saved state retained", tId );
				await RecordFailureAsync( tId, tException, true, tCancel );
			}
		}

		private async Task SaveRecoveryAsync( long tId, ReviewRecovery tRecovery, CancellationToken tCancel )
		{
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				PullRequest? tempRequest = await tempContext.PullRequest.FindAsync( [ tId ], tCancel );

				if ( tempRequest?.Status != PullRequestStatus.Processing )
				{
					throw new ReviewFailureException( "cancelled", "Review state changed during recovery" );
				}

				tRecovery.Save( tempRequest );
				tempRequest.LastError = tRecovery.Action;
				await tempContext.SaveChangesAsync( tCancel );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}

		private async Task RecordFailureAsync( long tId, Exception tException, bool tIsApprovalCleared, CancellationToken tCancel )
		{
			using IServiceScope tempScope = tScopes.CreateScope();
			Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				PullRequest? tempRequest = await tempContext.PullRequest.FindAsync( [ tId ], tCancel );

				if ( tempRequest is not null && tempRequest.Status is PullRequestStatus.Processing or PullRequestStatus.ReadyForApproval )
				{
					if ( tIsApprovalCleared )
					{
						// Require fresh approval after a publication failure
						tempRequest.ApprovedSha = null;
					}

					if ( tempRequest.Status == PullRequestStatus.Processing )
					{
						ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );
						string tempCategory = tException is ReviewFailureException tempFailure ? tempFailure.Category : tException is TimeoutException || ReviewFailureException.IsTransient( tException.Message ) ? "transient" : "infrastructure";

						if ( !tIsApprovalCleared && tempCategory is "transient" or "interrupted" or "required_check" or "unfinished_review" or "missing_evidence" && tempRecovery.Attempts < 2 && tempRecovery.OperationId is not null && tempRequest.WorkReason is not ( PullRequestWorkReason.ManualTweak or PullRequestWorkReason.Interactive or PullRequestWorkReason.CiRecovery or PullRequestWorkReason.ReportRetry ) )
						{
							tempRecovery.IsPending = true;
							tempRecovery.IsContinuation = tempRecovery.IsContinuation || tempCategory is "required_check" or "unfinished_review" or "missing_evidence";
							tempRecovery.Category = tempCategory;
							tempRecovery.Action = tempRecovery.IsContinuation ? "Continuing saved review work and rerunning affected validation: " + tException.Message[ ..Math.Min( 1450, tException.Message.Length ) ] : "Temporary interruption; checking the worker before continuing: " + tException.Message[ ..Math.Min( 1500, tException.Message.Length ) ];
							tempRecovery.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds( 30 );
							tempRecovery.Save( tempRequest );
							tempRequest.LastError = tempRecovery.Action;
						}
						else
						{
							string tempReason = tempRecovery.Attempts >= 2 && tempCategory is "transient" or "interrupted" ? $"Automatic recovery exhausted two retries. {tException.Message} Use connect to inspect the retained work" : tException.Message;
							WorkflowRules.Block( tempRequest, tempReason, tempCategory );
						}
					}
					else
					{
						tempRequest.LastError = tException.Message[ ..Math.Min( 4000, tException.Message.Length ) ];
					}
					await tempContext.SaveChangesAsync( tCancel );
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}
	}
}
