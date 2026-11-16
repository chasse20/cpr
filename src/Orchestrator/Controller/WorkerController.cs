using CPR.Service.Queue;
using CPR.SQL;

namespace CPR.Controller
{
	[ApiController]
	[Route( "api/worker" )]
	public sealed class WorkerController( Context tContext, QueueGate tGate ) : ControllerBase
	{
		public sealed record StatusUpdate( string Status );

		[HttpGet]
		public async Task<IActionResult> GetAsync( CancellationToken tCancel )
		{
			return Ok( await tContext.Worker.AsNoTracking().OrderBy( x => x.WorkerId ).Select( x => new
			{
				Id = x.WorkerId,
				x.SshHost,
				x.Status,
				x.LastSeenAt,
				PullRequestId = x.PullRequest == null ? (long?)null : x.PullRequest.PullRequestId
			} ).ToListAsync( tCancel ) );
		}

		[HttpPut( "{id:int}/status" )]
		public async Task<IActionResult> SetStatusAsync( [FromRoute( Name = "id" )] int tId, [FromBody] StatusUpdate tUpdate, CancellationToken tCancel )
		{
			if ( !Enum.TryParse( tUpdate.Status, true, out WorkerStatus tempStatus ) || !Enum.IsDefined( tempStatus ) )
			{
				return BadRequest( new
				{
					Error = "Unknown worker status"
				} );
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				Worker? tempWorker = await tContext.Worker.FindAsync( [ tId ], tCancel );

				if ( tempWorker is null )
				{
					return NotFound();
				}

				tempWorker.Status = tempStatus;
				await tContext.SaveChangesAsync( tCancel );

				return Ok( new
				{
					Id = tempWorker.WorkerId,
					tempWorker.Status
				} );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}
	}
}
