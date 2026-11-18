using CPR.Worker.Git;
using CPR.Worker.Persistence;

namespace CPR.Worker.Execution
{
	public sealed class ModelRunner( Workspace tWorkspace, string tOperation )
	{
		public async Task<int> RunAsync( ReviewStep tStep, string tDirectory, string tAttempt, string tPrompt, string tSchema, bool tIsWritable, string? tArtifacts, string? tSession, CancellationToken tCancel )
		{
			ProcessControl.RequireOperation( tOperation );
			Directory.CreateDirectory( tAttempt );
			Storage.WriteText( Path.Combine( tAttempt, "prompt.txt" ), tPrompt );
			List<string> tempArguments = [ "codex", "--ask-for-approval", "never" ];
			if ( tIsWritable )
			{
				tempArguments.AddRange( [ "-c", "sandbox_workspace_write.network_access=true" ] );
			}
			tempArguments.AddRange( [ "exec", "--sandbox", tIsWritable ? "workspace-write" : "read-only" ] );
			if ( tIsWritable )
			{
				tempArguments.AddRange( [ "--add-dir", "/state/cargo", "--add-dir", tArtifacts! ] );
			}
			else
			{
				tempArguments.Add( "--skip-git-repo-check" );
			}
			if ( !string.IsNullOrEmpty( tSession ) )
			{
				tempArguments.Add( "resume" );
			}
			tempArguments.AddRange( [ "--json", "--model", tStep.Model, "-c", $"model_reasoning_effort=\"{tStep.ReasoningEffort}\"", "--output-schema", tSchema, "--output-last-message", Path.Combine( tAttempt, "result.json" ) ] );
			if ( !string.IsNullOrEmpty( tSession ) )
			{
				tempArguments.Add( tSession );
			}
			tempArguments.Add( "-" );
			ProcessStartInfo tempInfo = Commands.CreateStartInfo( "setsid", tempArguments, tDirectory );
			tempInfo.Environment[ "CPR_REVIEW_OPERATION" ] = tOperation;
			using Process tempProcess = new() { StartInfo = tempInfo };
			string tempRegistration = Path.Combine( tWorkspace.Job, "process-" + tOperation );
			await using FileStream tempEvents = File.Create( Path.Combine( tAttempt, "events.jsonl" ) );
			await using FileStream tempError = File.Create( Path.Combine( tAttempt, "codex.stderr" ) );
			Task tempOutputTask = Task.CompletedTask;
			Task tempErrorTask = Task.CompletedTask;
			bool tempIsStarted = false;
			int tempCode = 143;
			try
			{
				using ( WorkerLock tempControl = await ProcessControl.LockControlAsync( Path.Combine( tWorkspace.Job, "control-" + tOperation ), tCancel ) )
				{
					RequireNotCancelled();
					tCancel.ThrowIfCancellationRequested();
					tempProcess.Start();
					tempIsStarted = true;
					tempOutputTask = tempProcess.StandardOutput.BaseStream.CopyToAsync( tempEvents );
					tempErrorTask = tempProcess.StandardError.BaseStream.CopyToAsync( tempError );
					string[]? tempIdentity = ProcessControl.ReadIdentity( tempProcess.Id );
					Storage.WriteText( tempRegistration, $"{tempProcess.Id} {tempIdentity?[ 19 ] ?? "0"}" );
				}
				await tempProcess.StandardInput.WriteAsync( tPrompt.AsMemory(), tCancel );
				tempProcess.StandardInput.Close();
				await tempProcess.WaitForExitAsync( tCancel );
				tempCode = tempProcess.ExitCode;
			}
			finally
			{
				if ( tempIsStarted )
				{
					if ( !tempProcess.HasExited )
					{
						await ProcessControl.StopGroupAsync( tempProcess.Id );
						if ( !tempProcess.HasExited )
						{
							tempProcess.Kill( true );
						}
					}
					await tempProcess.WaitForExitAsync( CancellationToken.None );
					await Task.WhenAll( tempOutputTask, tempErrorTask );
					File.Delete( tempRegistration );
				}
				await tempEvents.DisposeAsync();
				await tempError.DisposeAsync();
				Storage.Write( Path.Combine( tAttempt, "usage.json" ), Usage.Read( Path.Combine( tAttempt, "events.jsonl" ), tempCode ), true );
			}
			RequireNotCancelled();

			return tempCode;
		}

		public void RequireNotCancelled()
		{
			WorkerFailure.Require( !File.Exists( Path.Combine( tWorkspace.Job, "cancel-" + tOperation ) ), "Cancelled by user; use connect or review to continue", "cancelled" );
		}

		public static string Schema( string tRun, string tName )
		{
			string tempPath = Path.Combine( tRun, tName );
			using Stream tempStream = typeof( ModelRunner ).Assembly.GetManifestResourceStream( "CPR.Worker." + tName ) ?? throw new InvalidOperationException( $"Missing schema {tName}" );
			using StreamReader tempReader = new( tempStream );
			Storage.WriteText( tempPath, tempReader.ReadToEnd() );

			return tempPath;
		}
	}
}
