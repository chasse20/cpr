using System.Runtime.InteropServices;
using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;
using CPR.Worker.Recovery;
using CPR.Worker.Review;
using CPR.Worker.Sessions;

using CancellationTokenSource tempCancellation = new();
using PosixSignalRegistration tempTerm = PosixSignalRegistration.Create( PosixSignal.SIGTERM, x => { x.Cancel = true; tempCancellation.Cancel(); } );
using PosixSignalRegistration tempHangup = PosixSignalRegistration.Create( PosixSignal.SIGHUP, x => { x.Cancel = true; tempCancellation.Cancel(); } );
Console.CancelKeyPress += ( tSender, tEvent ) => { tEvent.Cancel = true; tempCancellation.Cancel(); };

try
{
	WorkerFailure.Require( args.Length == 1, "Expected one worker operation", "infrastructure" );
	string tempOperation = args[ 0 ];
	if ( tempOperation == "health" )
	{
		foreach ( string tempExecutable in new[] { "git", "codex" } )
		{
			CommandResult tempResult = await Commands.RunAsync( tempExecutable, [ "--version" ], tCancel: tempCancellation.Token );
			WorkerFailure.Require( tempResult.IsSuccess, $"Missing worker dependency: {tempExecutable}", "infrastructure" );
		}
		Console.WriteLine( "CPR_WORKER|1" );
	}
	else
	{
		WorkerRequest tempRequest = await JsonSerializer.DeserializeAsync<WorkerRequest>( Console.OpenStandardInput(), cancellationToken: tempCancellation.Token ) ?? throw new JsonException( "Missing worker request" );
		Workspace tempWorkspace = new( tempRequest );
		if ( tempOperation == "inspect" )
		{
			Storage.Emit( "CPR_STATE", await ReviewInspection.InspectAsync( tempWorkspace, tempRequest.OperationId!, tempCancellation.Token ) );
		}
		else if ( tempOperation == "cancel" )
		{
			await ProcessControl.CancelAsync( tempWorkspace, tempRequest.OperationId!, tempCancellation.Token );
			Console.WriteLine( "CPR_CANCELLED" );
		}
		else
		{
			using WorkerLock tempLock = new( Path.Combine( tempWorkspace.State, "worker.lock" ) );
			WorkerFailure.Require( tempLock.IsAcquired && !ProcessControl.IsAnyAlive( tempWorkspace.State ), "Worker is busy; preserving its active operation", "infrastructure" );
			if ( tempOperation is "run" or "prepare" or "manual-tweak" or "commit" or "park" )
			{
				await tempWorkspace.PrepareAsync( tempCancellation.Token );
			}
			switch ( tempOperation )
			{
				case "run":
					Storage.Emit( "CPR_SNAPSHOT", await new ReviewRunner( new( tempWorkspace, tempRequest.Plan! ) ).RunAsync( tempCancellation.Token ) );
					break;
				case "prepare":
					await new ManualSession( tempWorkspace ).PrepareAsync( tempCancellation.Token );
					Console.WriteLine( "CPR_SESSION|" + Storage.ReadText( Path.Combine( tempWorkspace.Job, "session-id" ) ) );
					break;
				case "manual-tweak":
					Storage.Emit( "CPR_SNAPSHOT", await new ManualSession( tempWorkspace ).CompleteAsync( tempRequest.Manual!, tempCancellation.Token ) );
					break;
				case "commit":
					Console.WriteLine( "CPR_COMMIT|" + await tempWorkspace.CommitAsync( tempRequest.ApprovedTree!, tempCancellation.Token ) );
					break;
				case "release":
					tempWorkspace.RequireActive();
					Storage.WriteText( Path.Combine( tempWorkspace.Job, "published" ), await tempWorkspace.GitAsync( [ "rev-parse", "HEAD" ], tempCancellation.Token ) );
					tempWorkspace.Park();
					Console.WriteLine( "CPR_RELEASED" );
					break;
				case "park":
					tempWorkspace.Park();
					break;
				case "detach":
					tempWorkspace.Detach();
					break;
				case "cleanup":
					tempWorkspace.Cleanup();
					Console.WriteLine( "CPR_CLEANED" );
					break;
				case "history":
					ReviewHistory.Emit( tempWorkspace, tempRequest.KnownHistory ?? [] );
					break;
				default:
					throw new WorkerFailure( "infrastructure", "Unknown worker operation" );
			}
		}
	}
	return 0;
}
catch ( Exception tException )
{
	string tempCategory = tException is WorkerFailure tempFailure ? tempFailure.Category : tException is OperationCanceledException ? "interrupted" : "infrastructure";
	string tempAction = tException is OperationCanceledException ? "Worker operation interrupted; reconcile before retrying" : tException.Message;
	Console.Error.WriteLine( "CPR_FAILURE|" + Convert.ToBase64String( JsonSerializer.SerializeToUtf8Bytes( new { category = tempCategory, action = tempAction } ) ) );
	Console.Error.WriteLine( tempAction );

	return tException is WorkerFailure tempError ? tempError.ExitCode : tException is OperationCanceledException ? 143 : 1;
}
