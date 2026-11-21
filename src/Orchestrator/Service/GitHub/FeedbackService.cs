using CPR.Service.Queue;
using CPR.Service.Review;
using CPR.SQL;

namespace CPR.Service.GitHub
{
	public sealed class FeedbackService( IServiceScopeFactory tScopes, QueueGate tGate, GitHubClient tGitHub, IOptions<Settings> tSettings, ILogger<FeedbackService> tLogger ) : BackgroundService
	{
		private readonly Settings _settings = tSettings.Value;

		protected override async Task ExecuteAsync( CancellationToken tCancel )
		{
			await Task.Delay( TimeSpan.FromSeconds( 5 ), tCancel );

			while ( !tCancel.IsCancellationRequested )
			{
				try
				{
					if ( _settings.IsCodeRabbitEnabled && _settings.IsWorkerExecutionEnabled && _settings.FeedbackRepairStep.IsEnabled )
					{
						using IServiceScope tempScope = tScopes.CreateScope();
						Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
						List<PullRequest> tempRequests = await tempContext.PullRequest.AsNoTracking().Where( x => x.Status != PullRequestStatus.Merged && x.Status != PullRequestStatus.Closed && x.Status != PullRequestStatus.Removed ).ToListAsync( tCancel );
						tGitHub.PruneFeedbackCache( tempRequests.Select( x => (x.Repository, x.Number) ) );

						foreach ( PullRequest tempRequest in tempRequests )
						{
							try
							{
								await PollAsync( tempRequest, tCancel );
							}
							catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
							{
								tLogger.LogWarning( "CodeRabbit poll for PR {Id} failed: {Reason}", tempRequest.PullRequestId, tException.Message );
								await SaveErrorAsync( tempRequest.PullRequestId, tException.Message, tCancel );
								// Back off on API failures instead of spinning through the same rate limit
								await Task.Delay( TimeSpan.FromSeconds( 60 ), tCancel );
								break;
							}
						}
					}
				}
				catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
				{
					break;
				}
				catch ( Exception tException )
				{
					tLogger.LogError( tException, "CodeRabbit polling failed; will retry" );
				}

				await Task.Delay( TimeSpan.FromSeconds( _settings.CodeRabbitPollSeconds ), tCancel );
			}
		}

		private async Task PollAsync( PullRequest tObserved, CancellationToken tCancel )
		{
			FeedbackComment[] tempComments = await tGitHub.ReadFeedbackAsync( tObserved.Repository, tObserved.Number, tCancel );
			FeedbackState tempState = JsonSerializer.Deserialize<FeedbackState>( tObserved.ReviewFeedback )!;
			DateTimeOffset tempNow = DateTimeOffset.UtcNow;
			tempState.Observe( tempComments, tempNow );
			tempState.LastError = null;
			bool tempIsSettled = false;
			GitHubPullRequest? tempRemote = null;

			if ( tempState.Pending.Count > 0 && IsEligible( tObserved ) )
			{
				(string tempHead, bool? tempIsComplete) = await tGitHub.ReadCodeRabbitStatusAsync( tObserved.Repository, tObserved.Number, tCancel );
				tempIsSettled = tempState.IsSettled( tempIsComplete, tempHead, tempNow, _settings.CodeRabbitQuietSeconds, _settings.CodeRabbitFallbackQuietSeconds );

				if ( tempIsSettled )
				{
					tempRemote = await tGitHub.ReadAsync( $"https://github.com/{tObserved.Repository}/pull/{tObserved.Number}", tCancel );
					tempIsSettled = tempRemote.HeadSha == tempHead;
				}
			}

			bool tempIsQueued = false;
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				PullRequest? tempRequest = await tempContext.PullRequest.FindAsync( [ tObserved.PullRequestId ], tCancel );

				if ( tempRequest is null || tempRequest.Status is PullRequestStatus.Merged or PullRequestStatus.Closed or PullRequestStatus.Removed || tempRequest.ReviewFeedback != tObserved.ReviewFeedback )
				{
					return;
				}

				if ( tempIsSettled && tempRemote is not null && IsEligible( tempRequest ) && !tGate.IsRunning( tempRequest.PullRequestId ) && tempRequest.RemoteSha == tObserved.RemoteSha && tempRequest.PublishedSha == tObserved.PublishedSha )
				{
					if ( !WorkflowRules.IsSameBranch( tempRequest, tempRemote ) || ( !WorkflowRules.IsSameSource( tempRequest, tempRemote ) && !WorkflowRules.IsFollowingCI( tempRequest ) ) )
					{
						tempState.LastError = "CodeRabbit feedback is pending, but the PR source changed; reconcile the saved workspace first";
					}
					else if ( await ReviewComment.FindFullReportAsync( tempContext, tempRequest, tCancel ) is null )
					{
						tempState.LastError = "CodeRabbit feedback is pending until a completed original review report is available";
					}
					else
					{
						tempState.Active = [ .. tempState.Pending.Values.OrderBy( x => x.Key, StringComparer.Ordinal ) ];
						tempState.LastBatch = [ .. tempState.Active.Select( x => x.Key ) ];
						tempState.Pending.Clear();
						tempState.LastAttemptAt = tempNow;
						tempRequest.RemoteSha = tempRemote.HeadSha;
						tempRequest.Status = PullRequestStatus.WaitingToBeProcessed;
						tempRequest.WorkReason = PullRequestWorkReason.FeedbackRepair;
						tempRequest.Recovery = "{}";
						tempRequest.PreparedSha = null;
						tempRequest.PreparedReviewRunId = null;
						tempRequest.ReviewedTargetSha = null;
						tempRequest.ApprovedSha = null;
						tempRequest.LastError = $"CodeRabbit feedback settled; queued one focused repair for {tempState.Active.Length} new or edited comments";
						tempIsQueued = true;
					}
				}

				// The watcher owns feedback state; completed jobs only need their compact history
				if ( tempRequest.Status is not ( PullRequestStatus.Processing or PullRequestStatus.WaitingToBeProcessed ) )
				{
					tempState.Active = [];
				}

				tempRequest.ReviewFeedback = JsonSerializer.Serialize( tempState );
				await tempContext.SaveChangesAsync( tCancel );
			}
			finally
			{
				tGate.Mutex.Release();
			}

			if ( tempIsQueued )
			{
				tLogger.LogInformation( "Queued CodeRabbit repair for PR {Id} with {Count} comments", tObserved.PullRequestId, tempState.Active.Length );
				tGate.RequestScheduling();
			}
		}

		private static bool IsEligible( PullRequest tRequest )
		{
			return tRequest.WorkerId is null && tRequest.ApprovedSha is null && tRequest.CleanupRequestedAt is null && ReviewRecovery.Read( tRequest ).Category != "cancelled" &&
				tRequest.Status is PullRequestStatus.ReadyForApproval or PullRequestStatus.Blocked or PullRequestStatus.WaitingForCi or PullRequestStatus.CiFailure or PullRequestStatus.WaitingForMergeTurn or PullRequestStatus.ReadyToBeMerged;
		}

		private async Task SaveErrorAsync( long tId, string tError, CancellationToken tCancel )
		{
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				PullRequest? tempRequest = await tempContext.PullRequest.FindAsync( [ tId ], tCancel );

				if ( tempRequest is not null )
				{
					FeedbackState tempState = JsonSerializer.Deserialize<FeedbackState>( tempRequest.ReviewFeedback )!;
					tempState.LastError = tError[ ..Math.Min( 2000, tError.Length ) ];
					tempRequest.ReviewFeedback = JsonSerializer.Serialize( tempState );
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
