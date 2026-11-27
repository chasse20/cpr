using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;

namespace CPR.Worker.Recovery
{
	public static class ReviewInspection
	{
		public static async Task<WorkerReviewState> InspectAsync( Workspace tWorkspace, string tOperation, CancellationToken tCancel )
		{
			ProcessControl.RequireOperation( tOperation );
			using WorkerLock tempLock = new( Path.Combine( tWorkspace.State, "worker.lock" ) );
			if ( !tempLock.IsAcquired || ProcessControl.IsAnyAlive( tWorkspace.State ) )
			{
				return new( "running", null, "Waiting for the existing worker operation to finish", null );
			}
			if ( File.Exists( Path.Combine( tWorkspace.Job, "cancel-" + tOperation ) ) )
			{
				return new( "failed", "cancelled", "Cancelled by user; use connect or review to continue", null );
			}
			string tempRun = Storage.ReadText( Path.Combine( tWorkspace.Job, "operation-" + tOperation ) );
			if ( tempRun.Length == 0 )
			{
				return new( "idle", "transient", "Worker launch was not completed; retrying preparation", null );
			}
			Storage.RequireCanonical( tempRun );
			WorkerFailure.Require( Path.GetDirectoryName( tempRun ) == Path.Combine( tWorkspace.Job, "runs" ) && Regex.IsMatch( Path.GetFileName( tempRun ), "^[A-Za-z0-9_-]+$" ) && Storage.Read<ReviewPlan>( Path.Combine( tempRun, "plan.json" ) ).OperationId == tOperation, "Invalid saved operation" );
			JsonObject tempManifest = Storage.ReadObject( Path.Combine( tempRun, "manifest.json" ) );
			if ( tempManifest[ "status" ]?.GetValue<string>() == "complete" && File.Exists( Path.Combine( tempRun, "snapshot.json" ) ) )
			{
				WorkerSnapshot tempSnapshot = Storage.Read<WorkerSnapshot>( Path.Combine( tempRun, "snapshot.json" ) );
				string tempTree = await tWorkspace.FingerprintAsync( tCancel, tWorkspace.Stored );
				if ( tempTree != tempSnapshot.TreeSha || tempTree != Storage.ReadText( Path.Combine( tWorkspace.Job, "tree" ) ) || await tWorkspace.GitAsync( [ "rev-parse", "HEAD" ], tCancel, tWorkspace.Stored ) != Storage.ReadText( Path.Combine( tWorkspace.Job, "base-sha" ) ) )
				{
					return new( "failed", "snapshot", "Saved workspace changed after completion; use connect to reconcile it", null );
				}

				return new( "complete", null, null, tempSnapshot );
			}
			if ( tempManifest[ "status" ]?.GetValue<string>() is "failed" or "cancelled" )
			{
				return new( "failed", tempManifest[ "failure" ]?[ "category" ]?.GetValue<string>() ?? "infrastructure", tempManifest[ "failure" ]?[ "action" ]?.GetValue<string>() ?? "Worker stopped without a recoverable classification; inspect its retained run", null );
			}

			string? tempHead = tempManifest[ "head_sha" ]?.GetValue<string>();
			string? tempTarget = tempManifest[ "target_sha" ]?.GetValue<string>();
			if ( tempRun != Storage.ReadText( Path.Combine( tWorkspace.Job, "review-checkpoint" ) ) || tempHead is null || tempTarget is null )
			{
				return new( "idle", "transient", "Worker stopped before a review checkpoint was established", null );
			}

			Workspace.RequireSha( tempHead );
			Workspace.RequireSha( tempTarget );
			if ( !Directory.Exists( tWorkspace.Stored ) || Storage.ReadText( Path.Combine( tWorkspace.Job, "target-branch" ) ) != tWorkspace.Request.TargetBranch || Storage.ReadText( Path.Combine( tWorkspace.Job, "target-sha" ) ) != tempTarget || !( await tWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", tempHead, "HEAD" ], tCancel, tWorkspace.Stored ) ).IsSuccess )
			{
				return new( "failed", "snapshot", "Interrupted review no longer matches the saved workspace; use connect to reconcile it", null );
			}

			string tempMerge = Storage.ReadText( Path.Combine( tWorkspace.Stored, ".git/MERGE_HEAD" ) );
			bool tempIsIntegrated = tempMerge == tempTarget || ( tempMerge.Length == 0 && ( await tWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", tempTarget, "HEAD" ], tCancel, tWorkspace.Stored ) ).IsSuccess );

			return tempIsIntegrated
				? new( "idle", "interrupted", "Verified saved checkpoint; resuming interrupted review work", null, tempHead )
				: new( "failed", "snapshot", "Interrupted review no longer has its recorded target integration; use connect to reconcile it", null );
		}
	}
}
