using CPR.Worker.Git;

namespace CPR.Worker.Persistence
{
	public static class ReviewHistory
	{
		public static void Emit( Workspace tWorkspace, Dictionary<string, string> tKnown )
		{
			string tempRuns = Path.Combine( tWorkspace.Job, "runs" );
			Storage.RequireCanonical( tempRuns );
			if ( Directory.Exists( tempRuns ) )
			{
				foreach ( string tempRun in Directory.EnumerateDirectories( tempRuns ).Order( StringComparer.Ordinal ) )
				{
					Storage.RequireCanonical( tempRun );
					string tempManifestPath = Path.Combine( tempRun, "manifest.json" );
					if ( File.Exists( tempManifestPath ) )
					{
						string[] tempDirectories = [ .. Directory.EnumerateDirectories( tempRun ).Order( StringComparer.Ordinal ) ];
						string[] tempAttempts = [ .. tempDirectories.Select( x => Path.Combine( x, "attempt.json" ) ).Where( File.Exists ) ];
						string[] tempResults = [ .. tempDirectories.Select( x => Path.Combine( x, "result.json" ) ).Where( File.Exists ) ];
						List<string> tempFiles = [ tempManifestPath, .. tempAttempts, .. tempResults ];
						tempFiles.AddRange( new[] { Path.Combine( tempRun, "result.json" ), Path.Combine( tempRun, "changes.stat" ) }.Where( File.Exists ) );
						string tempFingerprint = Storage.Fingerprint( tempFiles );
						string tempKey = Path.GetFileName( tempRun );
						if ( tKnown.GetValueOrDefault( tempKey ) != tempFingerprint )
						{
							JsonArray tempAttemptData = [];
							foreach ( string tempAttempt in tempAttempts )
							{
								JsonObject tempRecord = ReadObject( tempAttempt );
								string tempResult = Path.Combine( Path.GetDirectoryName( tempAttempt )!, "result.json" );
								tempRecord[ "result" ] = File.Exists( tempResult ) ? ReadObject( tempResult ) : null;
								tempAttemptData.Add( tempRecord );
							}
							string? tempFinal = File.Exists( Path.Combine( tempRun, "result.json" ) ) ? Path.Combine( tempRun, "result.json" ) : tempResults.LastOrDefault();
							JsonObject tempManifest = ReadObject( tempManifestPath );
							tempManifest.Remove( "attempts" );
							tempManifest.Remove( "current_attempt" );
							tempManifest.Remove( "scratch" );
							Storage.Emit( "CPR_HISTORY", new { key = tempKey, fingerprint = tempFingerprint, manifest = tempManifest, attempts = tempAttemptData, result = tempFinal is null ? null : ReadObject( tempFinal ), changes = Storage.ReadText( Path.Combine( tempRun, "changes.stat" ) ) } );
						}
					}
				}
			}
			Console.WriteLine( "CPR_HISTORY_DONE" );
		}

		private static JsonObject ReadObject( string tPath )
		{
			Storage.RequireCanonical( tPath );
			try
			{
				return Storage.ReadObject( tPath );
			}
			catch ( JsonException )
			{
				return new() { [ "capture_error" ] = "Incomplete or invalid JSON retained in this run" };
			}
		}
	}
}
