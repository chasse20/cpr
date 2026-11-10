using CPR.Service.Queue;
using CPR.SQL;

namespace CPR.Controller
{
	[ApiController]
	[Route( "api/prompt" )]
	public sealed class PromptController( Context tContext, QueueGate tGate ) : ControllerBase
	{
		public sealed record PromptUpdate( string Content );
		public sealed record PromptResponse( PromptKind Kind, string Content );

		[HttpGet]
		public async Task<IActionResult> GetAsync( CancellationToken tCancel )
		{
			return Ok( await tContext.Prompt.AsNoTracking().OrderBy( x => x.PromptId ).Select( x => new PromptResponse( x.PromptId, x.Content ) ).ToListAsync( tCancel ) );
		}

		[HttpGet( "{kind}" )]
		public async Task<IActionResult> GetAsync( [FromRoute( Name = "kind" )] string tKind, CancellationToken tCancel )
		{
			if ( !Enum.TryParse( tKind, true, out PromptKind tempKind ) || !Enum.IsDefined( tempKind ) )
			{
				return BadRequest();
			}

			Prompt? tempPrompt = await tContext.Prompt.FindAsync( [ tempKind ], tCancel );

			return tempPrompt is null ? NotFound() : Ok( new PromptResponse( tempPrompt.PromptId, tempPrompt.Content ) );
		}

		[HttpPut( "{kind}" )]
		public async Task<IActionResult> UpdateAsync( [FromRoute( Name = "kind" )] string tKind, [FromBody] PromptUpdate tUpdate, CancellationToken tCancel )
		{
			if ( !Enum.TryParse( tKind, true, out PromptKind tempKind ) || !Enum.IsDefined( tempKind ) )
			{
				return BadRequest();
			}

			if ( string.IsNullOrWhiteSpace( tUpdate.Content ) || tUpdate.Content.Length > 30000 )
			{
				return BadRequest( new
				{
					Error = "Prompt must be nonempty and <= 30000 characters"
				} );
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				Prompt? tempPrompt = await tContext.Prompt.FindAsync( [ tempKind ], tCancel );

				if ( tempPrompt is null )
				{
					return NotFound();
				}

				tempPrompt.Content = tUpdate.Content;
				await tContext.SaveChangesAsync( tCancel );

				return Ok( new PromptResponse( tempPrompt.PromptId, tempPrompt.Content ) );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}
	}
}
