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

		protected override async Task ExecuteAsync( CancellationToken tCancel )
		{
			await Task.Delay( TimeSpan.FromSeconds( 2 ), tCancel );
			bool tempIsRecovered = false;

			while ( !tCancel.IsCancellationRequested )
			{
				try
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
					await ScheduleAsync( tCancel );
				}
				catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
				{
					break;
				}
				catch ( Exception tException )
				{
					tLogger.LogError( tException, "Orchestration poll failed; will retry" );
				}

				await Task.Delay( TimeSpan.FromSeconds( Math.Max( 15, _settings.PollSeconds ) ), tCancel );
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

				foreach ( PullRequest tempRequest in tempInterrupted.Where( x => !tGate.Running.ContainsKey( x.PullRequestId ) ) )
				{
					WorkflowRules.Block( tempRequest, "Orchestrator restarted with unfinished or unpinned work; review or connect to continue" );
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
					await tContext.SaveChangesAsync( tCancel );

					return;
				}

				if ( tempSnapshot.State != "OPEN" )
				{
					tLogger.LogWarning( "Unrecognized GitHub state for PR {Id}: {State}", tId, tempSnapshot.State );

					return;
				}

				// Discard polls overtaken by queue actions or a publication in progress
				if ( tempObserved is null || tempRequest.Status != tempObserved.Status || tempRequest.RemoteSha != tempObserved.RemoteSha || tempRequest.TargetBranch != tempObserved.TargetBranch || tempRequest.PublishedSha != tempObserved.PublishedSha || ( tempRequest.ApprovedSha is not null && tGate.Running.ContainsKey( tId ) ) )
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
				else if ( !WorkflowRules.IsSameSource( tempRequest, tempSnapshot ) || ( tempRequest.Status == PullRequestStatus.ReadyForApproval && tempRequest.ReviewedTargetSha != tempSnapshot.TargetSha ) )
				{
					WorkflowRules.Block( tempRequest, "PR head or target changed since review; use cpr connect --tweak for manual target integration, or cpr review to reassess" );
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
							.Where( x => x.Repository == tempRequest.Repository && x.TargetBranch == tempRequest.TargetBranch && x.Status != PullRequestStatus.Merged && x.Status != PullRequestStatus.Closed && x.Status != PullRequestStatus.Removed )
							.OrderBy( x => x.PullRequestId ).Select( x => x.PullRequestId ).FirstAsync( tCancel );
						WorkflowDecision tempDecision = WorkflowRules.Decide( tempRequest, tempFrontId == tempRequest.PullRequestId, tempSnapshot, tempChecks );

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
			PullRequestStatus[] tempStatuses = [ PullRequestStatus.ReadyForApproval, PullRequestStatus.Blocked, PullRequestStatus.Removed, PullRequestStatus.WaitingForCi, PullRequestStatus.CiFailure, PullRequestStatus.WaitingForMergeTurn, PullRequestStatus.ReadyToBeMerged ];
			List<long> tempIds = await tempContext.PullRequest.AsNoTracking().Where( x => tempStatuses.Contains( x.Status ) && x.CleanupRequestedAt == null && x.ApprovedSha == null && x.WorkerId != null ).Select( x => x.PullRequestId ).ToListAsync( tCancel );

			foreach ( long tempId in tempIds )
			{
				PullRequest? tempRequest;
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					tempRequest = await tempContext.PullRequest.Include( x => x.Worker ).AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tempId, tCancel );

					if ( tempRequest?.Worker is null || !tempStatuses.Contains( tempRequest.Status ) || tempRequest.CleanupRequestedAt is not null || tempRequest.ApprovedSha is not null || !tGate.Running.TryAdd( tempId, Task.CompletedTask ) )
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

					try
					{
						await tHistory.CaptureAsync( tempRequest.Worker, tempRequest, tCancel );

						if ( tempIsPublished )
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
								tempCurrent.WorkspaceWorkerId = tempIsPublished ? null : tempRequest.WorkerId;
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
						tGate.Running.TryRemove( tempId, out _ );
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
				List<PullRequest> tempJobs = await tempContext.PullRequest
					.Where( x => x.WorkerId == null && ( x.Status == PullRequestStatus.WaitingToBeProcessed || ( _settings.IsPublicationEnabled && x.Status == PullRequestStatus.ReadyForApproval && x.ApprovedSha != null ) ) )
					.OrderBy( x => x.ApprovedSha != null ? 0 : x.WorkReason == PullRequestWorkReason.ManualTweak || x.WorkReason == PullRequestWorkReason.Interactive || x.WorkReason == PullRequestWorkReason.CiRecovery || x.WorkReason == PullRequestWorkReason.Rereview || x.WorkReason == PullRequestWorkReason.ReportRetry ? 1 : x.WorkReason == PullRequestWorkReason.MergeConflict ? 2 : 3 )
					.ThenBy( x => x.PullRequestId ).ToListAsync( tCancel );
				List<PullRequest> tempReviews = [];

				foreach ( Worker tempWorker in tempFree.Where( x => !tGate.CleaningWorkers.Contains( x.WorkerId ) ) )
				{
					PullRequest? tempRequest = tempJobs.FirstOrDefault( x => x.WorkerId is null && !tGate.Running.ContainsKey( x.PullRequestId ) && ( x.WorkspaceWorkerId is null || x.WorkspaceWorkerId == tempWorker.WorkerId ) );

					if ( tempRequest is not null )
					{
						if ( tempRequest.WorkspaceWorkerId is null )
						{
							tempRequest.SessionId = null;
						}

						tempRequest.StorageWorkerIds = [ .. tempRequest.StorageWorkerIds.Append( tempWorker.WorkerId ).Distinct() ];
						tempRequest.WorkerId = tempWorker.WorkerId;
						tempRequest.WorkspaceWorkerId = tempWorker.WorkerId;
						tempRequest.LastError = null;

						if ( tempRequest.Status == PullRequestStatus.WaitingToBeProcessed )
						{
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
					Task tempTask = Task.Run( () => ProcessJobAsync( tempId, tempWorkerId, tCancel ), CancellationToken.None );

					tGate.Running[ tempId ] = tempTask;
					_ = tempTask.ContinueWith( x => { tGate.Running.TryRemove( tempId, out _ ); }, TaskScheduler.Default );
				}

				if ( _settings.IsPublicationEnabled )
				{
					List<long> tempApprovals = await tempContext.PullRequest.AsNoTracking()
						.Where( x => x.Status == PullRequestStatus.ReadyForApproval && x.ApprovedSha != null && x.WorkerId != null )
						.Select( x => x.PullRequestId ).ToListAsync( tCancel );

					foreach ( long tempId in tempApprovals )
					{
						if ( tGate.Running.TryAdd( tempId, Task.CompletedTask ) )
						{
							Task tempTask = Task.Run( () => PublishJobAsync( tempId, tCancel ), CancellationToken.None );

							tGate.Running[ tempId ] = tempTask;
							_ = tempTask.ContinueWith( x => { tGate.Running.TryRemove( tempId, out _ ); }, TaskScheduler.Default );
						}
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

				if ( tempRequest.WorkReason is PullRequestWorkReason.CiRecovery or PullRequestWorkReason.Interactive or PullRequestWorkReason.ManualTweak )
				{
					// Interactive work starts only after cpr connect
					await tWorkers.PrepareInteractiveAsync( tempWorker, tempRequest, tCancel );
					tLogger.LogInformation( "PR {Id} prepared for interactive work on worker {Worker}", tId, tWorkerId );

					return;
				}

				ReviewService tempReview = tempScope.ServiceProvider.GetRequiredService<ReviewService>();
				WorkerSnapshot tempResult = await tempReview.ExecuteAsync( tempWorker, tempRequest, false, tCancel );
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
				await RecordFailureAsync( tId, tException.Message, false, tCancel );
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
				string tempCommit = await tWorkers.CommitApprovedAsync( tempWorker, tempRequest, tCancel );

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
				await RecordFailureAsync( tId, tException.Message, true, tCancel );
			}
		}

		private async Task RecordFailureAsync( long tId, string tMessage, bool tIsApprovalCleared, CancellationToken tCancel )
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
						WorkflowRules.Block( tempRequest, tMessage );
					}
					else
					{
						tempRequest.LastError = tMessage[ ..Math.Min( 4000, tMessage.Length ) ];
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
