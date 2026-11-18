using CPR.Service;
using CPR.Service.GitHub;
using CPR.Service.Queue;
using CPR.Service.Review;
using CPR.Service.SSH;
using CPR.SQL;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CPR.Controller
{
	[ApiController]
	[Route( "api/pr" )]
	public sealed class PullRequestController( Context tContext, QueueGate tGate, GitHubClient tGitHub, WorkerClient tWorkers, ReviewService tReviews, ReviewHistoryService tHistory, IOptions<Settings> tSettings ) : ControllerBase
	{
		public sealed record AddRequest( string Url );
		public sealed record ReviewRequest( [MaxLength( 16000 )] string? Resolution );

		[HttpGet( "/api/status" )]
		public async Task<IActionResult> GetStatusAsync( CancellationToken tCancel )
		{
			return Ok( await tContext.PullRequest.AsNoTracking().Where( x => x.CleanedAt == null || ( x.Status != PullRequestStatus.Merged && x.Status != PullRequestStatus.Closed && x.Status != PullRequestStatus.Removed ) ).OrderBy( x => x.PullRequestId ).Select( x => new { Id = x.PullRequestId, x.Number, x.Repository, x.TargetBranch, x.Status, x.CompletedAt, x.CleanupRequestedAt, x.CleanedAt, x.StorageWorkerIds, x.LastError } ).ToListAsync( tCancel ) );
		}

		[HttpPost]
		public async Task<IActionResult> AddAsync( [FromBody] AddRequest tRequest, CancellationToken tCancel )
		{
			if ( string.IsNullOrWhiteSpace( tRequest.Url ) )
			{
				return BadRequest( new
				{
					Error = "Provide one GitHub PR URL"
				} );
			}

			GitHubPullRequest tempIncoming;

			try
			{
				tempIncoming = await tGitHub.ReadAsync( tRequest.Url, tCancel );

				if ( tempIncoming.State != "OPEN" )
				{
					return BadRequest( new
					{
						Error = $"PR not open: {tRequest.Url}"
					} );
				}
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				return BadRequest( new
				{
					Error = tException.Message
				} );
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				PullRequest? tempCurrent = await tContext.PullRequest.SingleOrDefaultAsync( x => x.Repository == tempIncoming.Repository && x.Number == tempIncoming.Number, tCancel );

				if ( tempCurrent is null )
				{
					tempCurrent = new()
					{
						Repository = tempIncoming.Repository,
						Number = tempIncoming.Number,
						TargetBranch = tempIncoming.TargetBranch,
						HeadRepository = tempIncoming.HeadRepository,
						HeadBranch = tempIncoming.HeadBranch,
						RemoteSha = tempIncoming.HeadSha,
						Status = PullRequestStatus.WaitingToBeProcessed,
						WorkReason = PullRequestWorkReason.InitialReview
					};
					tContext.PullRequest.Add( tempCurrent );
					await tContext.SaveChangesAsync( tCancel );
				}
				else if ( tempCurrent.Status is PullRequestStatus.Closed or PullRequestStatus.Removed )
				{
					if ( ( tempCurrent.CleanupRequestedAt is not null && tempCurrent.CleanedAt is null ) || tempCurrent.WorkerId is not null || tGate.IsRunning( tempCurrent.PullRequestId ) )
					{
						return Conflict( new
						{
							Error = "The saved PR is still releasing or cleaning its worker; retry after that finishes"
						} );
					}

					tempCurrent.TargetBranch = tempIncoming.TargetBranch;
					tempCurrent.HeadRepository = tempIncoming.HeadRepository;
					tempCurrent.HeadBranch = tempIncoming.HeadBranch;
					tempCurrent.RemoteSha = tempIncoming.HeadSha;
					tempCurrent.Status = PullRequestStatus.WaitingToBeProcessed;
					tempCurrent.WorkReason = PullRequestWorkReason.InitialReview;
					tempCurrent.Recovery = "{}";
					tempCurrent.CompletedAt = null;
					tempCurrent.CleanedAt = null;
					tempCurrent.CleanupRequestedAt = null;
					tempCurrent.PreparedSha = null;
					tempCurrent.PreparedReviewRunId = null;
					tempCurrent.ApprovedSha = null;
					tempCurrent.ReviewedTargetSha = null;
					tempCurrent.LastError = null;
					await tContext.SaveChangesAsync( tCancel );
				}

				return Ok( new
				{
					Id = tempCurrent.PullRequestId,
					tempCurrent.Number,
					tempCurrent.Repository,
					tempCurrent.TargetBranch,
					tempCurrent.Status
				} );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}

		[HttpGet( "{id:long}" )]
		public async Task<IActionResult> ShowAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			PullRequest? tempRequest = await tContext.PullRequest.AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

			if ( tempRequest is null )
			{
				return NotFound();
			}

			List<ReviewRun> tempRuns = await tContext.ReviewRun.AsNoTracking().Where( x => x.PullRequestId == tId ).OrderBy( x => x.RunKey ).ThenBy( x => x.ReviewRunId ).ToListAsync( tCancel );
			ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );
			string tempNext = tempRequest.Status switch
			{
				PullRequestStatus.Merged or PullRequestStatus.Closed => tempRequest.CleanedAt is null ? "Automatic file cleanup pending" : "Complete; database history retained",
				PullRequestStatus.Removed => tempRequest.CleanupRequestedAt is not null && tempRequest.CleanedAt is null ? "File cleanup pending" : "Use review to resume tracking, or purge to delete saved files",
				PullRequestStatus.Blocked => tempRecovery.Action ?? "Use connect to resolve the recorded blocker; review explicitly requests a full reassessment",
				PullRequestStatus.AwaitingDecision => tempRecovery.Action ?? "Provide a maintainer resolution with cpr review ID --resolution TEXT",
				PullRequestStatus.NoLongerNeeded => tempRecovery.Action ?? "Review the saved relevance evidence; remove tracking or request a new review if the PR still has a distinct purpose",
				PullRequestStatus.ReadyForApproval => tempRequest.ApprovedSha is null ? "Inspect the review, then approve to integrate the latest target and publish" : tempRequest.WorkerId is null ? "Approved; waiting for an available worker to publish" : tempRequest.WorkerId != tempRequest.WorkspaceWorkerId ? $"Approved; transferring the saved checkout to worker {tempRequest.WorkerId}" : $"Approved; preparing publication on worker {tempRequest.WorkerId}",
				PullRequestStatus.CiFailure => "CI reruns are polled automatically; use review or connect for code repairs",
				PullRequestStatus.ReadyToBeMerged => "Merge on GitHub when ready; CPR does not merge automatically",
				PullRequestStatus.WaitingToBeProcessed => tempRecovery.Action ?? "Waiting for a worker",
				PullRequestStatus.Processing => tempRecovery.Action ?? "Work in progress; cancel stops an automated review and preserves its edits",
				_ => "Waiting for the queue or GitHub checks"
			};

			return Ok( new
			{
				Id = tempRequest.PullRequestId,
				tempRequest.Number,
				tempRequest.Repository,
				tempRequest.Status,
				tempRequest.WorkReason,
				tempRequest.WorkerId,
				tempRequest.WorkspaceWorkerId,
				tempRequest.StorageWorkerIds,
				tempRequest.RemoteSha,
				tempRequest.TargetBranch,
				tempRequest.ReviewedTargetSha,
				tempRequest.PreparedSha,
				tempRequest.PreparedReviewRunId,
				PrComment = tempRequest.Status == PullRequestStatus.ReadyForApproval ? tempRuns.SingleOrDefault( x => x.ReviewRunId == tempRequest.PreparedReviewRunId )?.CommentBody : null,
				tempRequest.ApprovedSha,
				tempRequest.PublishedSha,
				tempRequest.CompletedAt,
				tempRequest.CleanupRequestedAt,
				tempRequest.CleanedAt,
				tempRequest.LastError,
				tempRequest.MaintainerResolution,
				IsCodeRabbitEnabled = tSettings.Value.IsCodeRabbitEnabled && tSettings.Value.IsWorkerExecutionEnabled && tSettings.Value.FeedbackRepairStep.IsEnabled && tempRequest.Status is not ( PullRequestStatus.Merged or PullRequestStatus.Closed or PullRequestStatus.Removed or PullRequestStatus.AwaitingDecision or PullRequestStatus.NoLongerNeeded ),
				CodeRabbit = JsonSerializer.Deserialize<FeedbackState>( tempRequest.ReviewFeedback ),
				Recovery = tempRecovery,
				NextAction = tempNext,
				Runs = tempRuns.Select( x => new { x.ReviewRunId, x.WorkerId, x.PublishedSha, x.CommentId, x.CommentError, x.CommentAttemptedAt, x.CommentPublishedAt, Details = JsonSerializer.Deserialize<JsonElement>( x.Details ) } )
			} );
		}

		[HttpDelete( "{id:long}" )]
		public Task<IActionResult> RemoveAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			return ReviewOrRemoveAsync( tId, true, false, false, null, tCancel );
		}

		[HttpPost( "{id:long}/purge" )]
		public Task<IActionResult> PurgeAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			return ReviewOrRemoveAsync( tId, true, true, false, null, tCancel );
		}

		[HttpPost( "{id:long}/review" )]
		public Task<IActionResult> ReviewAsync( [FromRoute( Name = "id" )] long tId, [FromBody( EmptyBodyBehavior = EmptyBodyBehavior.Allow )] ReviewRequest? tRequest, CancellationToken tCancel )
		{
			return ReviewOrRemoveAsync( tId, false, false, false, tRequest?.Resolution, tCancel );
		}

		[HttpPost( "{id:long}/retry" )]
		public Task<IActionResult> RetryAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			return ReviewOrRemoveAsync( tId, false, false, true, null, tCancel );
		}

		private async Task<IActionResult> ReviewOrRemoveAsync( long tId, bool tIsRemoved, bool tIsPurge, bool tIsReportRetry, string? tResolution, CancellationToken tCancel )
		{
			PullRequest? tempRequest;
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				tempRequest = await tContext.PullRequest.Include( x => x.Worker ).Include( x => x.WorkspaceWorker ).SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempRequest is null )
				{
					return NotFound();
				}

				if ( tIsReportRetry && ( tempRequest.Status != PullRequestStatus.Blocked || tempRequest.WorkspaceWorkerId is null ) )
				{
					return Conflict( new { Error = "Report retry requires a blocked review with a saved workspace" } );
				}

				if ( !tIsPurge && ( tempRequest.Status is PullRequestStatus.Merged or PullRequestStatus.Closed || tempRequest.CleanupRequestedAt is not null && tempRequest.CleanedAt is null ) )
				{
					return Conflict( new { Error = "Completed PRs retain only database history; file cleanup must finish before reactivation" } );
				}

				if ( !tGate.TryBegin( tId ) )
				{
					return Conflict( new { Error = "Worker operation is running; cancel an automated review first, or wait for publication/control work to finish" } );
				}

				try
				{
					tempRequest.ApprovedSha = null;
					await tContext.SaveChangesAsync( tCancel );
					tContext.ChangeTracker.Clear();
				}
				catch
				{
					tGate.Complete( tId );

					throw;
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				GitHubPullRequest? tempRemote = tIsRemoved ? null : await tGitHub.ReadAsync( $"https://github.com/{tempRequest.Repository}/pull/{tempRequest.Number}", tCancel );

				if ( tempRemote is not null && tempRemote.State != "OPEN" )
				{
					return Conflict( new { Error = "PR is no longer open; tracked PRs will be completed and cleaned automatically" } );
				}

				if ( tIsReportRetry && !WorkflowRules.IsSameSource( tempRequest, tempRemote! ) )
				{
					return Conflict( new { Error = "PR head or destination changed; report retry cannot renew review coverage" } );
				}

				if ( tempRequest.Worker is not null )
				{
					await tHistory.CaptureAsync( tempRequest.Worker, tempRequest, tCancel );
					await tWorkers.DetachAsync( tempRequest.Worker, tempRequest, tCancel );
				}

				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest tempCurrent = await tContext.PullRequest.SingleAsync( x => x.PullRequestId == tId, tCancel );
					bool tempIsCompleted = tempCurrent.Status is PullRequestStatus.Merged or PullRequestStatus.Closed;

					if ( tempIsCompleted && !tIsPurge )
					{
						return Conflict( new { Error = "PR completed during this request; cleanup is automatic" } );
					}

					tempCurrent.WorkspaceWorkerId ??= tempCurrent.WorkerId;
					tempCurrent.WorkerId = null;
					tempCurrent.PreparedSha = null;
					tempCurrent.PreparedReviewRunId = null;
					tempCurrent.ApprovedSha = null;
					tempCurrent.ReviewedTargetSha = null;

					if ( tIsRemoved )
					{
						if ( !tempIsCompleted )
						{
							tempCurrent.Status = PullRequestStatus.Removed;
						}

						if ( tIsPurge )
						{
							tempCurrent.CleanupRequestedAt ??= DateTimeOffset.UtcNow;
						}

						tempCurrent.LastError = tIsPurge && tempCurrent.CleanedAt is null ? "File cleanup requested" : null;
					}
					else
					{
						tempCurrent.TargetBranch = tempRemote!.TargetBranch;
						tempCurrent.HeadRepository = tempRemote.HeadRepository;
						tempCurrent.HeadBranch = tempRemote.HeadBranch;
						tempCurrent.RemoteSha = tempRemote.HeadSha;
						tempCurrent.WorkReason = tIsReportRetry ? PullRequestWorkReason.ReportRetry : PullRequestWorkReason.Rereview;
						tempCurrent.Recovery = "{}";
						tempCurrent.Status = PullRequestStatus.WaitingToBeProcessed;
						tempCurrent.CompletedAt = null;
						tempCurrent.CleanupRequestedAt = null;
						tempCurrent.CleanedAt = null;
						tempCurrent.LastError = tIsReportRetry ? "Report retry requested; saved review and checks will be reused" : "Rereview requested; waiting for worker";

						if ( tResolution is not null )
						{
							tempCurrent.MaintainerResolution = string.IsNullOrWhiteSpace( tResolution ) ? null : tResolution;
						}
					}

					await tContext.SaveChangesAsync( tCancel );

					return Ok( new
					{
						Id = tId,
						Message = tIsPurge ? "File deletion queued, including unpublished edits; database history retained" : tIsRemoved ? "Tracking stopped; saved work and database history retained. Use purge to delete the files" : tIsReportRetry ? "Report retry queued; no code review or test rerun, fresh approval will be required" : "Rereview queued with the latest comments; fresh approval will be required"
					} );
				}
				finally
				{
					tGate.Mutex.Release();
				}
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				return Conflict( new { Error = $"Pending approval cleared; saved work retained: {tException.Message}" } );
			}
			finally
			{
				tGate.Complete( tId );
			}
		}

		[HttpPost( "{id:long}/cancel" )]
		public async Task<IActionResult> CancelAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			PullRequest? tempRequest;
			ReviewOperation? tempOperation;
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				tempRequest = await tContext.PullRequest.Include( x => x.Worker ).SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempRequest is null )
				{
					return NotFound();
				}

				tGate.Reviews.TryGetValue( tId, out tempOperation );
				ReviewRecovery tempRecovery = ReviewRecovery.Read( tempRequest );

				if ( tempOperation is null && tempRequest.Status == PullRequestStatus.Processing && tempRequest.ApprovedSha is null && tempRequest.WorkReason is not ( PullRequestWorkReason.ManualTweak or PullRequestWorkReason.Interactive or PullRequestWorkReason.CiRecovery ) && tempRecovery.OperationId is not null )
				{
					tempOperation = new( tempRecovery.OperationId );
				}

				if ( tempRequest.Status is PullRequestStatus.Merged or PullRequestStatus.Closed or PullRequestStatus.Removed || ( tGate.IsRunning( tId ) && tempOperation is null ) )
				{
					return Conflict( new { Error = "Only review work can be cancelled; publication, control operations, and terminal cleanup must finish" } );
				}

				WorkflowRules.Block( tempRequest, "Cancelled by user; saved work retained. Use review or connect to continue", "cancelled" );
				await tContext.SaveChangesAsync( tCancel );
			}
			finally
			{
				tGate.Mutex.Release();
			}

			if ( tempOperation is not null && tempRequest.Worker is not null )
			{
				try
				{
					using CancellationTokenSource tempStop = new( TimeSpan.FromSeconds( 60 ) );
					await tWorkers.CancelReviewAsync( tempRequest.Worker, tempRequest, tempOperation.Id, tempStop.Token );
				}
				catch ( Exception tException )
				{
					return Conflict( new { Error = $"Approval revoked, but worker cancellation was not confirmed; retry cancel: {tException.Message}" } );
				}
			}

			return Ok( new { Id = tId, Message = "Review cancelled; edits retained. The worker is released once its process exits; interactive sessions must be exited manually" } );
		}

		[HttpGet( "{id:long}/connection" )]
		public async Task<IActionResult> GetConnectionAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			PullRequest? tempRequest = await tContext.PullRequest.Include( x => x.Worker ).Include( x => x.WorkspaceWorker ).AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

			if ( tempRequest is null )
			{
				return NotFound();
			}

			if ( tempRequest.Status is PullRequestStatus.Merged or PullRequestStatus.Closed || tempRequest.CleanupRequestedAt is not null )
			{
				return Conflict( new
				{
					Error = "This PR has completed; its saved files are subject to automatic cleanup"
				} );
			}

			return Ok( GetConnection( tempRequest, true ) );
		}

		[HttpPost( "{id:long}/connect" )]
		public async Task<IActionResult> ConnectAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			PullRequest? tempRequest;

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				tempRequest = await tContext.PullRequest.Include( x => x.Worker ).SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempRequest is null )
				{
					return NotFound();
				}

				if ( tempRequest.Status is PullRequestStatus.Merged or PullRequestStatus.Closed or PullRequestStatus.Removed || tempRequest.CleanupRequestedAt is not null )
				{
					return Conflict( new
					{
						Error = "This PR has completed; interactive work is no longer available"
					} );
				}

				if ( tempRequest.ApprovedSha is not null || tGate.IsRunning( tId ) )
				{
					return Conflict( new
					{
						Error = "Worker operation is still running; connect after it finishes"
					} );
				}

				if ( tempRequest.WorkspaceWorkerId is null && tempRequest.PublishedSha is null )
				{
					return Conflict( new { Error = "Manual sessions require a saved workspace or a previously published CPR review" } );
				}

				tempRequest.WorkReason = PullRequestWorkReason.ManualTweak;
				tempRequest.Recovery = "{}";
				tempRequest.Status = tempRequest.WorkerId is null ? PullRequestStatus.WaitingToBeProcessed : PullRequestStatus.Processing;
				tempRequest.PreparedSha = null;
				tempRequest.PreparedReviewRunId = null;
				tempRequest.ReviewedTargetSha = null;
				tempRequest.LastError = tempRequest.Worker is null ? "Manual session requested; waiting for worker" : "Manual session requested; no automated review on completion";

				if ( tempRequest.Worker is null )
				{
					await tContext.SaveChangesAsync( tCancel );

					return Ok( GetConnection( tempRequest ) );
				}

				if ( !tGate.TryBegin( tId ) )
				{
					return Conflict( new
					{
						Error = "No active editable worker session"
					} );
				}

				try
				{
					await tContext.SaveChangesAsync( tCancel );
				}
				catch
				{
					tGate.Complete( tId );

					throw;
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				// Preserve interrupted work and verify the checkout before returning its SSH host
				string? tempSession = await tReviews.PrepareInteractiveAsync( tempRequest.Worker, tempRequest, tCancel );

				PullRequest? tempCurrent = await tContext.PullRequest.Include( x => x.Worker ).AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempCurrent is not null && tempCurrent.Status != PullRequestStatus.Processing )
				{
					return Conflict( new
					{
						Error = "PR state changed while preparing the interactive session"
					} );
				}

				if ( tempCurrent is not null && tempSession is not null )
				{
					tempCurrent.SessionId = tempSession;
				}

				return tempCurrent is null ? NotFound() : Ok( GetConnection( tempCurrent ) );
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				return Conflict( new
				{
					Error = tException.Message
				} );
			}
			finally
			{
				tGate.Complete( tId );
			}
		}

		[HttpPost( "{id:long}/complete" )]
		public async Task<IActionResult> CompleteAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			PullRequest? tempRequest;

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				tempRequest = await tContext.PullRequest.Include( x => x.Worker ).SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempRequest?.Worker is null || tempRequest.Status is not ( PullRequestStatus.Processing or PullRequestStatus.ReadyForApproval ) || tempRequest.ApprovedSha is not null )
				{
					return Conflict( new
					{
						Error = "No active editable worker session"
					} );
				}

				if ( tempRequest.WorkReason != PullRequestWorkReason.ManualTweak )
				{
					return Conflict( new { Error = "Reopen cpr connect to prepare a manual session before completing it" } );
				}

				if ( !tGate.TryBegin( tId ) )
				{
					return Conflict( new
					{
						Error = "Worker operation is still running"
					} );
				}

				tempRequest.Status = PullRequestStatus.Processing;
				tempRequest.PreparedSha = null;
				tempRequest.PreparedReviewRunId = null;
				tempRequest.ReviewedTargetSha = null;
				tempRequest.LastError = "Checking manual session snapshot";

				try
				{
					await tContext.SaveChangesAsync( tCancel );
					tContext.Entry( tempRequest ).State = EntityState.Detached;
				}
				catch
				{
					tGate.Complete( tId );

					throw;
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				WorkerSnapshot tempSnapshot = await tReviews.CompleteTweakAsync( tempRequest.Worker, tempRequest, tCancel );
				bool tempNeedsReview = tempRequest.PublishedSha is null && await ReviewComment.FindFullReportAsync( tContext, tempRequest, tCancel ) is null;

				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest? tempCurrent = await tContext.PullRequest.FindAsync( [ tId ], tCancel );

					if ( tempCurrent is null || tempCurrent.WorkerId != tempRequest.WorkerId || tempCurrent.ApprovedSha is not null || tempCurrent.Status is not ( PullRequestStatus.Processing or PullRequestStatus.ReadyForApproval ) )
					{
						return Conflict( new
						{
							Error = "Session or approval changed during validation"
						} );
					}

					tempCurrent.PreparedReviewRunId = tempNeedsReview ? null : await ReviewComment.PrepareAsync( tContext, tempCurrent, tempSnapshot, tCancel );
					tempCurrent.PreparedSha = tempNeedsReview ? null : tempSnapshot.TreeSha;
					tempCurrent.ReviewedTargetSha = tempSnapshot.TargetSha;
					tempCurrent.SessionId = tempSnapshot.SessionId ?? tempCurrent.SessionId;
					tempCurrent.WorkspaceWorkerId = tempRequest.WorkerId;
					tempCurrent.WorkerId = null;
					tempCurrent.Worker = null;
					tempCurrent.Status = tempNeedsReview ? PullRequestStatus.WaitingToBeProcessed : PullRequestStatus.ReadyForApproval;
					tempCurrent.LastError = null;

					if ( tempNeedsReview )
					{
						tempCurrent.WorkReason = PullRequestWorkReason.InitialReview;
						tempCurrent.ReviewedTargetSha = null;
						new ReviewRecovery { IsContinuation = true, Action = "Manual repairs saved; queued the remaining initial review work and affected validation" }.Save( tempCurrent );
					}

					await tContext.SaveChangesAsync( tCancel );

					return Ok( new
					{
						Id = tempCurrent.PullRequestId,
						tempCurrent.Status,
						tempCurrent.PreparedSha
					} );
				}
				finally
				{
					tGate.Mutex.Release();
				}
			}
			catch ( Exception tException )
			{
				using CancellationTokenSource tempRecord = new( TimeSpan.FromSeconds( 30 ) );
				await tGate.Mutex.WaitAsync( tempRecord.Token );

				try
				{
					tContext.ChangeTracker.Clear();
					PullRequest? tempCurrent = await tContext.PullRequest.FindAsync( [ tId ], tempRecord.Token );

					if ( tempCurrent is not null && tempCurrent.WorkerId == tempRequest.WorkerId && tempCurrent.Status == PullRequestStatus.Processing && tempCurrent.ApprovedSha is null )
					{
						WorkflowRules.Block( tempCurrent, tException.Message );
						await tContext.SaveChangesAsync( tempRecord.Token );
					}
				}
				finally
				{
					tGate.Mutex.Release();
				}

				return Conflict( new
				{
					Error = tException.Message
				} );
			}
			finally
			{
				tGate.Complete( tId );
			}
		}

		[HttpPost( "{id:long}/approve" )]
		public async Task<IActionResult> ApproveAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			if ( !tSettings.Value.IsPublicationEnabled )
			{
				return Conflict( new { Error = "Publication is disabled by configuration" } );
			}

			PullRequest? tempRequest;
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				tempRequest = await tContext.PullRequest.AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempRequest is null )
				{
					return NotFound();
				}

				if ( tempRequest.Status != PullRequestStatus.ReadyForApproval || tempRequest.WorkspaceWorkerId is null || tempRequest.PreparedSha is null || tempRequest.ReviewedTargetSha is null || tempRequest.ApprovedSha is not null )
				{
					return Conflict( new { Error = "PR must be ReadyForApproval with an unapproved saved snapshot" } );
				}

				if ( !tGate.TryBegin( tId ) )
				{
					return Conflict( new { Error = "Worker operation is still running" } );
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				GitHubPullRequest tempRemote = await tGitHub.ReadAsync( $"https://github.com/{tempRequest.Repository}/pull/{tempRequest.Number}", tCancel );
				await tGitHub.VerifyPublicationAccessAsync( tempRemote, tCancel );
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest tempCurrent = await tContext.PullRequest.SingleAsync( x => x.PullRequestId == tId, tCancel );

					if ( tempCurrent.Status != PullRequestStatus.ReadyForApproval || tempCurrent.PreparedSha != tempRequest.PreparedSha || tempCurrent.PreparedReviewRunId != tempRequest.PreparedReviewRunId || tempCurrent.ApprovedSha is not null )
					{
						return Conflict( new { Error = "Review state changed while checking approval" } );
					}

					if ( !WorkflowRules.IsSameSource( tempCurrent, tempRemote ) )
					{
						WorkflowRules.Block( tempCurrent, "PR head or destination changed; request a fresh review before approval" );
						await tContext.SaveChangesAsync( tCancel );

						return Conflict( new { Error = tempCurrent.LastError } );
					}

					await ReviewComment.GetPreparedAsync( tContext, tempCurrent, tCancel );

					tempCurrent.ApprovedSha = tempCurrent.PreparedSha;
					tempCurrent.LastError = null;
					await tContext.SaveChangesAsync( tCancel );

					return Ok( new { Id = tId, ApprovedTreeSha = tempCurrent.ApprovedSha, Message = "Approval recorded; CPR will preserve saved repairs, integrate the latest target if needed, and publish when integration checks pass" } );
				}
				finally
				{
					tGate.Mutex.Release();
				}
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				return Conflict( new { Error = $"Unable to verify approval: {tException.Message}" } );
			}
			finally
			{
				tGate.Complete( tId );
			}
		}

		private object GetConnection( PullRequest tRequest, bool tIsCheckout = false )
		{
			Worker? tempWorker = tRequest.Worker ?? ( tIsCheckout ? tRequest.WorkspaceWorker : null );

			return new
			{
				Id = tRequest.PullRequestId,
				tRequest.Status,
				tRequest.WorkReason,
				tRequest.WorkerId,
				tRequest.WorkspaceWorkerId,
				tRequest.SessionId,
				tRequest.TargetBranch,
				IsTweak = tRequest.WorkReason == PullRequestWorkReason.ManualTweak,
				SshHost = tempWorker?.SshHost,
				ProjectDirectory = tempWorker is null ? null : $"/workspace/jobs/{tRequest.PullRequestId}/repo",
				tRequest.LastError
			};
		}
	}
}
