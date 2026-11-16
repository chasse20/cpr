using System.Runtime.InteropServices;
using CPR.Worker.Git;
using CPR.Worker.Persistence;

namespace CPR.Worker.Execution
{
	public static class ProcessControl
	{
		public static string[]? ReadIdentity( int tPid )
		{
			try
			{
				string tempStat = File.ReadAllText( $"/proc/{tPid}/stat" );

				return tempStat[ ( tempStat.LastIndexOf( ") ", StringComparison.Ordinal ) + 2 ).. ].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
			}
			catch ( IOException )
			{
				return null;
			}
		}

		public static bool IsAlive( string tRegistration )
		{
			string[] tempSaved = Storage.ReadText( tRegistration ).Split( ' ', StringSplitOptions.RemoveEmptyEntries );
			if ( tempSaved.Length == 2 && int.TryParse( tempSaved[ 0 ], out int tempPid ) )
			{
				string[]? tempFields = ReadIdentity( tempPid );

				return tempFields is { Length: >= 20 } && tempFields[ 19 ] == tempSaved[ 1 ] && tempFields[ 0 ] is not ( "Z" or "X" );
			}

			return false;
		}

		public static bool IsAnyAlive( string tState )
		{
			return Directory.EnumerateDirectories( Path.Combine( tState, "jobs" ) ).Any( x => Directory.EnumerateFiles( x, "process-*" ).Any( IsAlive ) );
		}

		public static async Task<WorkerLock> LockControlAsync( string tPath, CancellationToken tCancel )
		{
			for ( int i = 0; i < 100; ++i )
			{
				WorkerLock tempLock = new( tPath );
				if ( tempLock.IsAcquired )
				{
					return tempLock;
				}
				tempLock.Dispose();
				await Task.Delay( 100, tCancel );
			}

			throw new WorkerFailure( "infrastructure", "Review process control is busy; retry cancellation", 12 );
		}

		public static async Task CancelAsync( Workspace tWorkspace, string tOperation, CancellationToken tCancel )
		{
			RequireOperation( tOperation );
			string tempRegistration = Path.Combine( tWorkspace.Job, "process-" + tOperation );
			string tempSaved;
			using ( WorkerLock tempControl = await LockControlAsync( Path.Combine( tWorkspace.Job, "control-" + tOperation ), tCancel ) )
			{
				Storage.WriteText( Path.Combine( tWorkspace.Job, "cancel-" + tOperation ), "" );
				tempSaved = Storage.ReadText( tempRegistration );
			}
			string[] tempIdentity = tempSaved.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
			if ( tempIdentity.Length == 2 && int.TryParse( tempIdentity[ 0 ], out int tempPid ) )
			{
				bool tempIsVerified = false;
				for ( int i = 0; i < 100 && IsAlive( tempRegistration ) && !tempIsVerified; ++i )
				{
					string[]? tempFields = ReadIdentity( tempPid );
					try
					{
						tempIsVerified = tempFields is { Length: >= 20 } && tempFields[ 19 ] == tempIdentity[ 1 ] && tempFields[ 2 ] == tempIdentity[ 0 ] && tempFields[ 3 ] == tempIdentity[ 0 ] && File.ReadAllText( $"/proc/{tempPid}/environ" ).Split( '\0' ).Contains( "CPR_REVIEW_OPERATION=" + tOperation );
					}
					catch ( IOException )
					{
						// The process can finish while its identity is being checked
					}
					if ( !tempIsVerified )
					{
						await Task.Delay( 50, tCancel );
					}
				}
				WorkerFailure.Require( tempIsVerified || !IsAlive( tempRegistration ), "Review process identity changed; no signal sent" );
				if ( tempIsVerified )
				{
					await StopGroupAsync( tempPid );
				}
			}
		}

		public static async Task StopGroupAsync( int tPid )
		{
			Kill( -tPid, 15 );
			for ( int i = 0; i < 150 && Kill( -tPid, 0 ) == 0; ++i )
			{
				await Task.Delay( 100 );
			}
			Kill( -tPid, 9 );
		}

		public static void RequireOperation( string tOperation )
		{
			WorkerFailure.Require( Regex.IsMatch( tOperation, "^[a-f0-9]{32}$" ), "Invalid review operation", "infrastructure" );
		}

		[DllImport( "libc", EntryPoint = "kill", SetLastError = true )]
		private static extern int Kill( int tPid, int tSignal );
	}
}
