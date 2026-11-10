using CPR.Service.GitHub;
using CPR.Service.SSH;
using CPR.SQL;

namespace CPR.Service.Queue
{
	public sealed class QueueService( IServiceScopeFactory tScopes, QueueGate tGate, GitHubClient tGitHub, WorkerClient tWorkers, GitPublisher tPublisher, IOptions<Settings> tSettings, ILogger<QueueService> tLogger ) : BackgroundService
	{
		private readonly Settings _settings = tSettings.Value;
		private readonly ConcurrentDictionary<long, Task> _running = new();

		protected override async Task ExecuteAsync( CancellationToken tCancel )
		{
			// Retain interrupted jobs after restart so existing Codex sessions stay reserved
			await Task.Delay( TimeSpan.FromSeconds( 2 ), tCancel );

			while ( !tCancel.IsCancellationRequested )
			{
				try
				{
					await CheckWorkersAsync( tCancel );
					await PollGitHubAsync( tCancel );
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
			List<PullRequest> tempRequests = await tempContext.PullRequest.AsNoTracking().ToListAsync( tCancel );

			foreach ( PullRequest tempRequest in tempRequests )
			{
				await PollPullRequestAsync( tempContext, tempRequest.PullRequestId, $"https://github.com/{tempRequest.Repository}/pull/{tempRequest.Number}", tCancel );
			}
		}

		private async Task PollPullRequestAsync( Context tContext, long tId, string tUrl, CancellationToken tCancel )
		{
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

			// Only published work can advance through CI
			CIResult tempChecks = CIResult.Pending;
			PullRequestStatus tempStatus = await tContext.PullRequest.AsNoTracking().Where( x => x.PullRequestId == tId ).Select( x => x.Status ).FirstOrDefaultAsync( tCancel );

			if ( tempStatus is PullRequestStatus.WaitingForCi or PullRequestStatus.WaitingForMergeTurn or PullRequestStatus.ReadyToBeMerged )
			{
				try
				{
					tempChecks = await tGitHub.ReadCIAsync( tUrl, _settings.IsCICheckRequired, tCancel );
				}
				catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
				{
					tLogger.LogWarning( "CI unavailable for PR {Id}: {Reason}", tId, tException.Message );

					return;
				}
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				PullRequest? tempRequest = await tContext.PullRequest.FindAsync( [ tId ], tCancel );

				if ( tempRequest is null )
				{
					return;
				}

				if ( tempSnapshot.State != "OPEN" )
				{
					// Reserve an occupied worker until its running or interrupted job is inspected
					if ( tempRequest.WorkerId is not null )
					{
						Worker? tempWorker = await tContext.Worker.FindAsync( [ tempRequest.WorkerId.Value ], tCancel );

						if ( tempWorker is not null )
						{
							tempWorker.Status = WorkerStatus.Paused;
						}
					}

					tContext.PullRequest.Remove( tempRequest );
					await tContext.SaveChangesAsync( tCancel );

					return;
				}

				if ( !string.Equals( tempRequest.TargetBranch, tempSnapshot.TargetBranch, StringComparison.Ordinal ) )
				{
					tempRequest.LastError = "Target branch changed; approval/merge lane must be reassessed";
					tempRequest.TargetBranch = tempSnapshot.TargetBranch;

					if ( tempRequest.Status is PullRequestStatus.WaitingForCi or PullRequestStatus.WaitingForMergeTurn or PullRequestStatus.ReadyToBeMerged )
					{
						tempRequest.Status = PullRequestStatus.CiFailure;
					}
				}

				// Refresh the upstream head only before work starts
				if ( tempRequest.Status == PullRequestStatus.WaitingToBeProcessed && tempRequest.WorkerId is null )
				{
					tempRequest.RemoteSha = tempSnapshot.HeadSha;
					tempRequest.HeadBranch = tempSnapshot.HeadBranch;
					tempRequest.HeadRepository = tempSnapshot.HeadRepository;
				}
				else if ( ( tempRequest.Status is PullRequestStatus.Processing or PullRequestStatus.ReadyForApproval ) && tempRequest.RemoteSha != tempSnapshot.HeadSha )
				{
					tempRequest.LastError = "PR head changed while a worker has it; re-review before publishing";
				}

				if ( tempRequest.Status is PullRequestStatus.WaitingForCi or PullRequestStatus.WaitingForMergeTurn or PullRequestStatus.ReadyToBeMerged )
				{
					if ( tempSnapshot.HeadSha != tempRequest.RemoteSha )
					{
						tempRequest.Status = PullRequestStatus.CiFailure;
						tempRequest.LastError = "Remote PR head changed outside CPR; connect to investigate";
						tempRequest.RemoteSha = tempSnapshot.HeadSha;
					}
					else
					{
						long tempFrontId = await tContext.PullRequest
							.Where( x => x.Repository == tempRequest.Repository && x.TargetBranch == tempRequest.TargetBranch )
							.OrderBy( x => x.PullRequestId ).Select( x => x.PullRequestId ).FirstAsync( tCancel );
						WorkflowDecision tempDecision = WorkflowRules.Decide( tempRequest, tempFrontId == tempRequest.PullRequestId, tempSnapshot, tempChecks );

						tempRequest.Status = tempDecision.Status;
						tempRequest.WorkReason = tempDecision.WorkReason;

						if ( tempDecision.Status == PullRequestStatus.WaitingToBeProcessed )
						{
							tempRequest.LastError = null;
						}
					}
				}

				await tContext.SaveChangesAsync( tCancel );
			}
			finally
			{
				tGate.Mutex.Release();
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
					.Where( x => x.Status == PullRequestStatus.WaitingToBeProcessed && x.WorkerId == null )
					.OrderBy( x => x.WorkReason == PullRequestWorkReason.MergeConflict ? 0 : x.WorkReason == PullRequestWorkReason.CiRecovery ? 1 : 2 )
					.ThenBy( x => x.PullRequestId ).Take( tempFree.Count ).ToListAsync( tCancel );

				foreach ( (PullRequest tempRequest, Worker tempWorker) in tempJobs.Zip( tempFree ) )
				{
					tempRequest.WorkerId = tempWorker.WorkerId;
					tempRequest.Status = PullRequestStatus.Processing;
					tempRequest.PreparedSha = null;
					tempRequest.ApprovedSha = null;
					tempRequest.LastError = null;
				}

				await tempContext.SaveChangesAsync( tCancel );

				foreach ( (PullRequest tempRequest, Worker tempWorker) in tempJobs.Zip( tempFree ) )
				{
					long tempId = tempRequest.PullRequestId;
					int tempWorkerId = tempWorker.WorkerId;
					Task tempTask = Task.Run( () => ProcessJobAsync( tempId, tempWorkerId, tCancel ), CancellationToken.None );

					_running[ tempId ] = tempTask;
					_ = tempTask.ContinueWith( x => { _running.TryRemove( tempId, out _ ); }, TaskScheduler.Default );
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}

			if ( !_settings.IsPublicationEnabled )
			{
				return;
			}

			using IServiceScope tempPublishScope = tScopes.CreateScope();
			Context tempPublishContext = tempPublishScope.ServiceProvider.GetRequiredService<Context>();
			List<long> tempApprovals = await tempPublishContext.PullRequest.AsNoTracking()
				.Where( x => x.Status == PullRequestStatus.ReadyForApproval && x.ApprovedSha != null && x.WorkerId != null )
				.Select( x => x.PullRequestId ).ToListAsync( tCancel );

			foreach ( long tempId in tempApprovals )
			{
				if ( _running.TryAdd( tempId, Task.CompletedTask ) )
				{
					Task tempTask = Task.Run( () => PublishJobAsync( tempId, tCancel ), CancellationToken.None );

					_running[ tempId ] = tempTask;
					_ = tempTask.ContinueWith( x => { _running.TryRemove( tempId, out _ ); }, TaskScheduler.Default );
				}
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

				if ( tempRequest.WorkReason == PullRequestWorkReason.CiRecovery )
				{
					// Interactive CI recovery starts only after cpr connect
					await tWorkers.PrepareInteractiveAsync( tempWorker, tempRequest, tCancel );
					tLogger.LogInformation( "PR {Id} prepared for interactive CI recovery on worker {Worker}", tId, tWorkerId );

					return;
				}

				PromptKind tempKind = tempRequest.WorkReason == PullRequestWorkReason.MergeConflict ? PromptKind.MergeConflict : PromptKind.InitialReview;
				Prompt tempPrompt = await tempContext.Prompt.AsNoTracking().SingleAsync( x => x.PromptId == tempKind, tCancel );
				WorkerSnapshot tempResult = await tWorkers.ExecuteAsync( tempWorker, tempRequest, PromptRenderer.Render( tempPrompt.Content, tempRequest ), tCancel );
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest? tempCurrent = await tempContext.PullRequest.FindAsync( [ tId ], tCancel );

					if ( tempCurrent is not null && tempCurrent.WorkerId == tWorkerId && tempCurrent.Status == PullRequestStatus.Processing )
					{
						tempCurrent.PreparedSha = tempResult.TreeSha;
						tempCurrent.SessionId = tempResult.SessionId ?? tempCurrent.SessionId;
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
				tLogger.LogError( tException, "Codex job {Id} failed; worker reserved for inspection", tId );
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
				string tempCommit = await tWorkers.CommitApprovedAsync( tempWorker, tempRequest, tCancel );

				await tPublisher.PushAsync( tempRequest, tempWorker, tempCommit, tCancel );
				await tWorkers.ReleaseAsync( tempWorker, tempRequest, tCancel );
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest? tempCurrent = await tempContext.PullRequest.FindAsync( [ tId ], tCancel );

					if ( tempCurrent is not null && tempCurrent.ApprovedSha == tempRequest.ApprovedSha && tempCurrent.WorkerId == tempRequest.WorkerId )
					{
						tempCurrent.PublishedSha = tempCommit;
						tempCurrent.RemoteSha = tempCommit;
						tempCurrent.PreparedSha = null;
						tempCurrent.ApprovedSha = null;
						tempCurrent.WorkerId = null;
						tempCurrent.Status = PullRequestStatus.WaitingForCi;
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
				tLogger.LogError( tException, "Approved push {Id} failed; no worker released", tId );
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

				if ( tempRequest is not null )
				{
					if ( tIsApprovalCleared )
					{
						// Require fresh approval after a publication failure
						tempRequest.ApprovedSha = null;
					}

					tempRequest.LastError = tMessage[ ..Math.Min( 4000, tMessage.Length ) ];
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
