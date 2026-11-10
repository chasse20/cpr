using CPR.Service;
using CPR.Service.GitHub;
using CPR.Service.Queue;
using CPR.Service.SSH;
using CPR.SQL;

namespace CPR.Controller
{
	[ApiController]
	[Route( "api/pr" )]
	public sealed class PullRequestController( Context tContext, QueueGate tGate, GitHubClient tGitHub, WorkerClient tWorkers, IOptions<Settings> tSettings ) : ControllerBase
	{
		public sealed record AddRequest( string Url );

		[HttpGet( "/api/status" )]
		public async Task<IActionResult> GetStatusAsync( CancellationToken tCancel )
		{
			return Ok( await tContext.PullRequest.AsNoTracking().OrderBy( x => x.PullRequestId ).Select( x => new { Id = x.PullRequestId, x.Number, x.Repository, x.TargetBranch, x.Status } ).ToListAsync( tCancel ) );
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

		[HttpGet( "{id:long}/connection" )]
		public async Task<IActionResult> GetConnectionAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			PullRequest? tempRequest = await tContext.PullRequest.Include( x => x.Worker ).AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

			return tempRequest is null ? NotFound() : Ok( GetConnection( tempRequest ) );
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

				if ( tempRequest.ApprovedSha is not null || tGate.Running.ContainsKey( tId ) )
				{
					return Conflict( new
					{
						Error = "Worker operation is still running; connect after it finishes"
					} );
				}

				if ( tempRequest.Status == PullRequestStatus.CiFailure )
				{
					tempRequest.Status = PullRequestStatus.WaitingToBeProcessed;
					tempRequest.WorkReason = PullRequestWorkReason.CiRecovery;
					tempRequest.PreparedSha = null;
					tempRequest.ApprovedSha = null;
					tempRequest.SessionId = null;
					tempRequest.LastError = "Interactive CI recovery requested; waiting for worker";
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
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				// Preserve interrupted work and verify the checkout before returning its SSH host
				await tWorkers.PrepareInteractiveAsync( tempRequest.Worker, tempRequest, tCancel );

				PullRequest? tempCurrent = await tContext.PullRequest.Include( x => x.Worker ).AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

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
				tempRequest = await tContext.PullRequest.Include( x => x.Worker ).AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

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
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				WorkerSnapshot tempSnapshot = await tWorkers.SnapshotAsync( tempRequest.Worker, tempRequest, tCancel );

				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest? tempCurrent = await tContext.PullRequest.FindAsync( [ tId ], tCancel );

					if ( tempCurrent is null || tempCurrent.WorkerId != tempRequest.WorkerId || tempCurrent.ApprovedSha is not null || tempCurrent.Status is not ( PullRequestStatus.Processing or PullRequestStatus.ReadyForApproval ) )
					{
						return Conflict( new
						{
							Error = "Session or approval changed during snapshot"
						} );
					}

					tempCurrent.PreparedSha = tempSnapshot.TreeSha;
					tempCurrent.SessionId = tempSnapshot.SessionId ?? tempCurrent.SessionId;
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

		[HttpPost( "{id:long}/approve" )]
		public async Task<IActionResult> ApproveAsync( [FromRoute( Name = "id" )] long tId, CancellationToken tCancel )
		{
			if ( !tSettings.Value.IsPublicationEnabled )
			{
				return Conflict( new
				{
					Error = "Publication is disabled by configuration"
				} );
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				PullRequest? tempRequest = await tContext.PullRequest.SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempRequest is null )
				{
					return NotFound();
				}

				if ( tempRequest.Status != PullRequestStatus.ReadyForApproval || tempRequest.WorkerId is null || tempRequest.PreparedSha is null || tempRequest.ApprovedSha is not null || tGate.Running.ContainsKey( tId ) )
				{
					return Conflict( new
					{
						Error = "PR must be ReadyForApproval with an unapproved snapshot"
					} );
				}

				if ( tempRequest.HeadRepository != tempRequest.Repository )
				{
					return Conflict( new
					{
						Error = "Fork PR head push not configured: cannot publish to a contributor fork"
					} );
				}

				tempRequest.ApprovedSha = tempRequest.PreparedSha;
				tempRequest.LastError = null;
				await tContext.SaveChangesAsync( tCancel );

				return Ok( new
				{
					Id = tempRequest.PullRequestId,
					ApprovedTreeSha = tempRequest.ApprovedSha,
					Message = "Approval recorded; the orchestrator will commit and push the exact approved tree"
				} );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}

		private static object GetConnection( PullRequest tRequest )
		{
			return new
			{
				Id = tRequest.PullRequestId,
				tRequest.Status,
				tRequest.WorkReason,
				tRequest.WorkerId,
				tRequest.SessionId,
				SshHost = tRequest.Worker?.SshHost,
				ProjectDirectory = tRequest.WorkerId is null ? null : "/workspace/repo",
				tRequest.LastError
			};
		}
	}
}
