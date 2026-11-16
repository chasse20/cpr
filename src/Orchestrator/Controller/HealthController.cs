using CPR.SQL;

namespace CPR.Controller
{
	[ApiController]
	[Route( "health" )]
	public sealed class HealthController( Context tContext ) : ControllerBase
	{
		[HttpGet]
		public IActionResult Get()
		{
			return Ok( new
			{
				Status = "Healthy"
			} );
		}

		[HttpGet( "db" )]
		public async Task<IActionResult> GetDatabaseAsync( CancellationToken tCancel )
		{
			bool tempIsConnected = await tContext.Database.CanConnectAsync( tCancel );

			return tempIsConnected ? Ok( new
			{
				Status = "Healthy"
			} ) : StatusCode( StatusCodes.Status503ServiceUnavailable );
		}
	}
}
