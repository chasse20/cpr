namespace CPR.Worker.Execution
{
	public sealed record Usage( long? InputTokens, long? CachedInputTokens, long? OutputTokens, bool IsComplete )
	{
		public static Usage Read( string tPath, int tExitCode )
		{
			long? tempInput = null;
			long? tempCached = null;
			long? tempOutput = null;
			int tempStarted = 0;
			int tempCompleted = 0;
			bool tempIsComplete = tExitCode == 0;
			foreach ( string tempLine in File.Exists( tPath ) ? File.ReadLines( tPath ) : [] )
			{
				try
				{
					using JsonDocument tempEvent = JsonDocument.Parse( tempLine );
					string? tempType = tempEvent.RootElement.GetProperty( "type" ).GetString();
					if ( tempType == "turn.started" )
					{
						++tempStarted;
					}
					else if ( tempType == "turn.completed" )
					{
						++tempCompleted;
						JsonElement tempUsage = tempEvent.RootElement.GetProperty( "usage" );
						long tempNextInput = tempUsage.GetProperty( "input_tokens" ).GetInt64();
						long tempNextCached = tempUsage.GetProperty( "cached_input_tokens" ).GetInt64();
						long tempNextOutput = tempUsage.GetProperty( "output_tokens" ).GetInt64();
						if ( tempNextInput >= 0 && tempNextCached >= 0 && tempNextCached <= tempNextInput && tempNextOutput >= 0 )
						{
							tempInput = checked( ( tempInput ?? 0 ) + tempNextInput );
							tempCached = checked( ( tempCached ?? 0 ) + tempNextCached );
							tempOutput = checked( ( tempOutput ?? 0 ) + tempNextOutput );
						}
						else
						{
							tempIsComplete = false;
						}
					}
					else if ( tempType is null or "turn.failed" or "error" )
					{
						tempIsComplete = false;
					}
				}
				catch ( Exception tException ) when ( tException is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException )
				{
					tempIsComplete = false;
				}
			}

			return new( tempInput, tempCached, tempOutput, tempIsComplete && tempCompleted > 0 && tempStarted == tempCompleted );
		}
	}
}
