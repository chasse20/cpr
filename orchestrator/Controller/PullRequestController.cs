using CPR.Service;
using CPR.Service.GitHub;
using CPR.Service.Queue;
using CPR.Service.Review;
using CPR.Service.SSH;
using CPR.SQL;

namespace CPR.Controller
{
	[ApiController]
	[Route( "api/pr" )]
	public sealed class PullRequestController( Context tContext, QueueGate tGate, GitHubClient tGitHub, WorkerClient tWorkers, ReviewService tReviews, ReviewHistoryService tHistory, IOptions<Settings> tSettings ) : ControllerBase
	{
		public sealed record AddRequest( string Url );

		[HttpGet( "/api/status" )]
		public async Task<IActionResult> GetStatusAsync( CancellationToken tCancel )
		{
			return Ok( await tContext.PullRequest.AsNoTracking().OrderBy( x => x.PullRequestId ).Select( x => new { Id = x.PullRequestId, x.Number, x.Repository, x.TargetBranch, x.Status, x.CompletedAt, x.CleanupRequestedAt, x.CleanedAt, x.StorageWorkerIds, x.LastError } ).ToListAsync( tCancel ) );
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
					if ( ( tempCurrent.CleanupRequestedAt is not null && tempCurrent.CleanedAt is null ) || tempCurrent.WorkerId is not null || tGate.Running.ContainsKey( tempCurrent.PullRequestId ) )
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
					tempCurrent.CompletedAt = null;
					tempCurrent.CleanedAt = null;
					tempCurrent.CleanupRequestedAt = null;
					tempCurrent.PreparedSha = null;
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
			string tempNext = tempRequest.Status switch
			{
				PullRequestStatus.Merged or PullRequestStatus.Closed => tempRequest.CleanedAt is null ? "Automatic file cleanup pending" : "Complete; database history retained",
				PullRequestStatus.Removed => tempRequest.CleanupRequestedAt is not null && tempRequest.CleanedAt is null ? "File cleanup pending" : "Use review to resume tracking, or purge to delete saved files",
				PullRequestStatus.Blocked => "Use review to reassess the latest PR, or connect to inspect and repair",
				PullRequestStatus.ReadyForApproval => tempRequest.ApprovedSha is null ? "Inspect this review and checkout, then approve or review again" : "Approved; waiting to publish",
				PullRequestStatus.CiFailure => "CI reruns are polled automatically; use review or connect for code repairs",
				PullRequestStatus.ReadyToBeMerged => "Merge on GitHub when ready; CPR does not merge automatically",
				PullRequestStatus.Processing => "Work in progress; cancel stops an automated review and preserves its edits",
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
				tempRequest.ApprovedSha,
				tempRequest.PublishedSha,
				tempRequest.CompletedAt,
				tempRequest.CleanupRequestedAt,
				tempRequest.CleanedAt,
				tempRequest.LastError,
				NextAction = tempNext,
				Runs = tempRuns.Select( x => new { x.WorkerId, Details = JsonSerializer.Deserialize<JsonElement>( x.Details ) } )
			} );
		}

		[HttpDelete( "{id:long}" )]
		public Task<IActionResult> RemoveAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			return ReviewOrRemoveAsync( tId, true, false, tCancel );
		}

		[HttpPost( "{id:long}/purge" )]
		public Task<IActionResult> PurgeAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			return ReviewOrRemoveAsync( tId, true, true, tCancel );
		}

		[HttpPost( "{id:long}/review" )]
		public Task<IActionResult> ReviewAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			return ReviewOrRemoveAsync( tId, false, false, tCancel );
		}

		private async Task<IActionResult> ReviewOrRemoveAsync( long tId, bool tIsRemoved, bool tIsPurge, CancellationToken tCancel )
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

				if ( !tIsPurge && ( tempRequest.Status is PullRequestStatus.Merged or PullRequestStatus.Closed || tempRequest.CleanupRequestedAt is not null && tempRequest.CleanedAt is null ) )
				{
					return Conflict( new { Error = "Completed PRs retain only database history; file cleanup must finish before reactivation" } );
				}

				if ( !tGate.Running.TryAdd( tId, Task.CompletedTask ) )
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
					tGate.Running.TryRemove( tId, out _ );

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
						tempCurrent.WorkReason = PullRequestWorkReason.Rereview;
						tempCurrent.Status = PullRequestStatus.WaitingToBeProcessed;
						tempCurrent.CompletedAt = null;
						tempCurrent.CleanupRequestedAt = null;
						tempCurrent.CleanedAt = null;
						tempCurrent.LastError = "Rereview requested; waiting for worker";
					}

					await tContext.SaveChangesAsync( tCancel );

					return Ok( new
					{
						Id = tId,
						Message = tIsPurge ? "File deletion queued, including unpublished edits; database history retained" : tIsRemoved ? "Tracking stopped; saved work and database history retained. Use purge to delete the files" : "Rereview queued with the latest comments; fresh approval will be required"
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
				tGate.Running.TryRemove( tId, out _ );
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

				if ( tempRequest.Status is PullRequestStatus.Merged or PullRequestStatus.Closed or PullRequestStatus.Removed || ( tGate.Running.ContainsKey( tId ) && tempOperation is null ) )
				{
					return Conflict( new { Error = "Only review work can be cancelled; publication, control operations, and terminal cleanup must finish" } );
				}

				WorkflowRules.Block( tempRequest, "Cancelled by user; saved work retained. Use review or connect to continue" );
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

				if ( tempRequest.ApprovedSha is not null || tGate.Running.ContainsKey( tId ) )
				{
					return Conflict( new
					{
						Error = "Worker operation is still running; connect after it finishes"
					} );
				}

				if ( tempRequest.Status is PullRequestStatus.CiFailure or PullRequestStatus.Blocked || ( tempRequest.Status == PullRequestStatus.ReadyForApproval && tempRequest.WorkerId is null ) )
				{
					tempRequest.WorkReason = tempRequest.Status == PullRequestStatus.CiFailure ? PullRequestWorkReason.CiRecovery : PullRequestWorkReason.Interactive;
					tempRequest.Status = tempRequest.WorkerId is null ? PullRequestStatus.WaitingToBeProcessed : PullRequestStatus.Processing;
					tempRequest.PreparedSha = null;
					tempRequest.ReviewedTargetSha = null;
					tempRequest.ApprovedSha = null;
					tempRequest.LastError = "Interactive work requested; waiting for worker";
					await tContext.SaveChangesAsync( tCancel );
				}

				if ( tempRequest.Worker is null )
				{
					return Ok( GetConnection( tempRequest ) );
				}

				if ( tempRequest.Status is not ( PullRequestStatus.Processing or PullRequestStatus.ReadyForApproval ) || !tGate.Running.TryAdd( tId, Task.CompletedTask ) )
				{
					return Conflict( new
					{
						Error = "No active editable worker session"
					} );
				}

				tempRequest.Status = PullRequestStatus.Processing;
				tempRequest.PreparedSha = null;
				tempRequest.ReviewedTargetSha = null;
				tempRequest.LastError = "Interactive session requested";

				try
				{
					await tContext.SaveChangesAsync( tCancel );
				}
				catch
				{
					tGate.Running.TryRemove( tId, out _ );

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
				string? tempSession = await tWorkers.PrepareInteractiveAsync( tempRequest.Worker, tempRequest, tCancel );

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
				tGate.Running.TryRemove( tId, out _ );
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

				if ( !tGate.Running.TryAdd( tId, Task.CompletedTask ) )
				{
					return Conflict( new
					{
						Error = "Worker operation is still running"
					} );
				}

				tempRequest.Status = PullRequestStatus.Processing;
				tempRequest.PreparedSha = null;
				tempRequest.ReviewedTargetSha = null;
				tempRequest.LastError = "Validating interactive edits";

				try
				{
					await tContext.SaveChangesAsync( tCancel );
					tContext.Entry( tempRequest ).State = EntityState.Detached;
				}
				catch
				{
					tGate.Running.TryRemove( tId, out _ );

					throw;
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				WorkerSnapshot tempSnapshot = await tReviews.ExecuteAsync( tempRequest.Worker, tempRequest, true, tCancel );

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

					tempCurrent.PreparedSha = tempSnapshot.TreeSha;
					tempCurrent.ReviewedTargetSha = tempSnapshot.TargetSha;
					tempCurrent.SessionId = tempSnapshot.SessionId ?? tempCurrent.SessionId;
					tempCurrent.WorkspaceWorkerId = tempRequest.WorkerId;
					tempCurrent.WorkerId = null;
					tempCurrent.Worker = null;
					tempCurrent.Status = PullRequestStatus.ReadyForApproval;
					tempCurrent.LastError = null;
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
				tGate.Running.TryRemove( tId, out _ );
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
					return Conflict( new { Error = "PR must be ReadyForApproval with an unapproved, current snapshot" } );
				}

				if ( !string.Equals( tempRequest.HeadRepository, tempRequest.Repository, StringComparison.OrdinalIgnoreCase ) )
				{
					return Conflict( new { Error = "Fork PR publication is not configured" } );
				}

				if ( !tGate.Running.TryAdd( tId, Task.CompletedTask ) )
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
				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest tempCurrent = await tContext.PullRequest.SingleAsync( x => x.PullRequestId == tId, tCancel );

					if ( tempCurrent.Status != PullRequestStatus.ReadyForApproval || tempCurrent.PreparedSha != tempRequest.PreparedSha || tempCurrent.ApprovedSha is not null )
					{
						return Conflict( new { Error = "Review state changed while checking approval" } );
					}

					if ( !WorkflowRules.IsSameSource( tempCurrent, tempRemote ) || tempCurrent.ReviewedTargetSha != tempRemote.TargetSha )
					{
						WorkflowRules.Block( tempCurrent, "PR head or target changed; request a fresh review before approval" );
						await tContext.SaveChangesAsync( tCancel );

						return Conflict( new { Error = tempCurrent.LastError } );
					}

					tempCurrent.ApprovedSha = tempCurrent.PreparedSha;
					tempCurrent.LastError = null;
					await tContext.SaveChangesAsync( tCancel );

					return Ok( new { Id = tId, ApprovedTreeSha = tempCurrent.ApprovedSha, Message = "Approval recorded; the orchestrator will reserve the saved workspace and publish the approved tree" } );
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
				tGate.Running.TryRemove( tId, out _ );
			}
		}

		private object GetConnection( PullRequest tRequest, bool tIsCheckout = false )
		{
			ReviewStepSettings tempValidation = tSettings.Value.ReviewSteps.Last( x => x.IsEnabled );
			Worker? tempWorker = tRequest.Worker ?? ( tIsCheckout ? tRequest.WorkspaceWorker : null );

			return new
			{
				Id = tRequest.PullRequestId,
				tRequest.Status,
				tRequest.WorkReason,
				tRequest.WorkerId,
				tRequest.WorkspaceWorkerId,
				tRequest.SessionId,
				CompletionTimeoutSeconds = ( tempValidation.TimeoutSeconds + 15 ) * tempValidation.MaxAttempts + 1800,
				SshHost = tempWorker?.SshHost,
				ProjectDirectory = tempWorker is null ? null : $"/workspace/jobs/{tRequest.PullRequestId}/repo",
				tRequest.LastError
			};
		}
	}
}
