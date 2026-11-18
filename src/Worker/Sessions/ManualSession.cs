using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;
using CPR.Worker.Reports;
using CPR.Worker.Review;

namespace CPR.Worker.Sessions
{
	public sealed class ManualSession( Workspace tWorkspace )
	{
		public async Task PrepareAsync( CancellationToken tCancel )
		{
			tWorkspace.RequireActive();
			string tempPath = Path.Combine( tWorkspace.Job, "tweak-base.json" );
			if ( !File.Exists( tempPath ) )
			{
				Storage.Write( tempPath, new { id = Guid.NewGuid().ToString(), head = await tWorkspace.GitAsync( [ "rev-parse", "HEAD" ], tCancel ), tree = await tWorkspace.FingerprintAsync( tCancel ), merge = Storage.ReadText( Path.Combine( tWorkspace.Root, ".git/MERGE_HEAD" ) ) } );
				File.Delete( Path.Combine( tWorkspace.Job, "tweak-checks.md" ) );
			}
		}

		public async Task<WorkerSnapshot> CompleteAsync( ManualPlan tPlan, CancellationToken tCancel )
		{
			tWorkspace.RequireActive();
			Workspace.RequireSha( tPlan.HeadSha );
			Workspace.RequireSha( tPlan.TargetSha );
			string tempHead = await tWorkspace.GitAsync( [ "rev-parse", "HEAD" ], tCancel );
			foreach ( string tempAncestor in new[] { tPlan.HeadSha, Storage.ReadText( Path.Combine( tWorkspace.Job, "base-sha" ) ) } )
			{
				WorkerFailure.Require( ( await tWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", tempAncestor, tempHead ], tCancel ) ).IsSuccess, "Manual checkout rewrote the saved history; preserving work" );
			}
			WorkerFailure.Require( ( await tWorkspace.GitAsync( [ "ls-files", "-u" ], tCancel ) ).Length == 0, "Resolve and stage remaining conflicts in connect before exiting" );
			WorkerFailure.Require( new[] { "rebase-merge", "rebase-apply", "CHERRY_PICK_HEAD", "REVERT_HEAD" }.All( x => !Path.Exists( Path.Combine( tWorkspace.Root, ".git", x ) ) ), "Finish the pending Git operation before exiting" );
			string tempMerge = Storage.ReadText( Path.Combine( tWorkspace.Root, ".git/MERGE_HEAD" ) );
			bool tempIsIntegrated = ( await tWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", tPlan.TargetSha, "HEAD" ], tCancel ) ).IsSuccess;
			foreach ( string tempParent in tempMerge.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
			{
				tempIsIntegrated |= ( await tWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", tPlan.TargetSha, tempParent ], tCancel ) ).IsSuccess;
			}
			WorkerFailure.Require( tempIsIntegrated, $"Latest target {tPlan.TargetSha} is not integrated; reopen connect and merge {tWorkspace.Request.TargetBranch}" );
			JsonObject tempEntry = Storage.ReadObject( Path.Combine( tWorkspace.Job, "tweak-base.json" ) );
			string tempEntryHead = tempEntry[ "head" ]!.GetValue<string>();
			string tempEntryTree = tempEntry[ "tree" ]!.GetValue<string>();
			Workspace.RequireSha( tempEntryHead );
			Workspace.RequireSha( tempEntryTree );
			WorkerFailure.Require( Guid.TryParse( tempEntry[ "id" ]?.GetValue<string>(), out Guid _ ), "Missing manual session entry identity" );
			await tWorkspace.GitAsync( [ "cat-file", "-e", tempEntryTree + "^{tree}" ], tCancel );
			WorkerFailure.Require( ( await tWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", tempEntryHead, tempHead ], tCancel ) ).IsSuccess, "Manual session rewrote its entry history" );
			string tempRun = Storage.CreateRun( tWorkspace.Job );
			string tempAttempt = Path.Combine( tempRun, "summary" );
			string tempSession = Storage.ReadText( Path.Combine( tWorkspace.Job, "session-id" ) );
			WorkerFailure.Require( tempSession.Length == 0 || Regex.IsMatch( tempSession, "^[A-Za-z0-9_-]{6,128}$" ), "Invalid saved session" );
			Storage.Write( Path.Combine( tempRun, "plan.json" ), tPlan );
			Storage.Write( Path.Combine( tempRun, "manifest.json" ), new { cpr_id = tWorkspace.Request.Id, url = tWorkspace.Url, kind = "manual_tweak", status = "running", started_at = DateTimeOffset.UtcNow, head_sha = tPlan.HeadSha, checkout_sha = tempHead, target_sha = tPlan.TargetSha, session_id = tempSession, maintainer_resolution = tPlan.MaintainerResolution, previous_published_sha = tPlan.PublishedSha, report_kind = tPlan.PublishedSha is null ? "full_review" : "commit_summary", tweak_session_id = tempEntry[ "id" ]!.GetValue<string>(), tweak_base_tree = tempEntryTree, tweak_base_head = tempEntryHead } );
			Storage.WriteText( Path.Combine( tWorkspace.Job, "latest-run" ), tempRun );
			int tempCode = 13;
			try
			{
				string tempTree = await tWorkspace.FingerprintAsync( tCancel );
				await tWorkspace.GitAsync( [ "diff", "--check", tPlan.HeadSha, tempTree ], tCancel );
				Workspace.RequireSafeNames( await tWorkspace.GitAsync( [ "diff", "--name-only", "-z", tPlan.HeadSha, tempTree ], tCancel ) );
				await tWorkspace.WriteGitAsync( Path.Combine( tempRun, "changes.patch" ), [ "diff", "--binary", tPlan.HeadSha, tempTree ], tCancel );
				await tWorkspace.WriteGitAsync( Path.Combine( tempRun, "changes.stat" ), [ "diff", "--stat", tPlan.HeadSha, tempTree ], tCancel );
				await tWorkspace.WriteGitAsync( Path.Combine( tempRun, "tweak.patch" ), [ "diff", "--binary", tempEntryTree, tempTree ], tCancel );
				await tWorkspace.WriteGitAsync( Path.Combine( tempRun, "tweak.stat" ), [ "diff", "--stat", tempEntryTree, tempTree ], tCancel );
				await tWorkspace.WriteGitAsync( Path.Combine( tempRun, "tweak-commits.txt" ), [ "log", "--format=fuller", tempEntryHead + ".." + tempHead ], tCancel );
				string tempChecks = Path.Combine( tWorkspace.Job, "tweak-checks.md" );
				if ( File.Exists( tempChecks ) )
				{
					Storage.RequireCanonical( tempChecks );
					File.Copy( tempChecks, Path.Combine( tempRun, "tweak-checks.md" ), true );
				}
				else
				{
					Storage.WriteText( Path.Combine( tempRun, "tweak-checks.md" ), "No checks were recorded for this tweak session.\n" );
				}
				ReviewStep tempStep = new( "tweak-summary", tPlan.SummaryModel, tPlan.SummaryReasoningEffort, 1, tPlan.SummaryPrompt );
				string tempPrompt = tPlan.SummaryPrompt + $"\nSession evidence:\n- Session delta only: {tempRun}/tweak.patch and {tempRun}/tweak.stat\n- Session commits: {tempRun}/tweak-commits.txt\n- Session checks: {tempRun}/tweak-checks.md\n- Entry head: {tempEntryHead}; entry tree: {tempEntryTree}; exit head: {tempHead}; exit tree: {tempTree}\n- Entry pending merge: {tempEntry[ "merge" ]}; exit pending merge: {tempMerge}\n- Latest target: {tWorkspace.Request.TargetBranch} at {tPlan.TargetSha}\n- Complete outgoing delta for commit_message only: {tempRun}/changes.patch and {tempRun}/changes.stat";
				Storage.Write( Path.Combine( tempAttempt, "attempt.json" ), new { step = "tweak-summary", attempt = 1, model = tempStep.Model, reasoning_effort = tempStep.ReasoningEffort, started_at = DateTimeOffset.UtcNow, input_tree = tempTree, status = "running" } );
				int tempModelCode = await new ModelRunner( tWorkspace, Guid.NewGuid().ToString( "N" ) ).RunAsync( tempStep, tWorkspace.Root, tempAttempt, tempPrompt, ModelRunner.Schema( tempRun, "tweak-summary.schema.json" ), false, null, null, tCancel );
				ReviewRunner.RequireModelSuccess( tempModelCode, Path.Combine( tempAttempt, "codex.stderr" ) );
				ManualSummary tempSummary = Storage.Read<ManualSummary>( Path.Combine( tempAttempt, "result.json" ), true );
				WorkerFailure.Require( ReportValidator.IsValidComment( tempSummary.Summary, 2000, true ) && ReportValidator.IsValidComment( tempSummary.PrComment, 4000, true ) && ReportValidator.IsValidCommitMessage( tempSummary.CommitMessage ), "Manual summary is invalid; preserving work", "report_metadata" );
				WorkerFailure.Require( await tWorkspace.GitAsync( [ "rev-parse", "HEAD" ], tCancel ) == tempHead && await tWorkspace.FingerprintAsync( tCancel ) == tempTree && Storage.ReadText( Path.Combine( tWorkspace.Root, ".git/MERGE_HEAD" ) ) == tempMerge, "Checkout changed while summarizing; reopen connect" );
				JsonObject tempResult = JsonSerializer.SerializeToNode( tempSummary, Storage.ReportOptions )!.AsObject();
				tempResult[ "outcome" ] = "complete";
				tempResult[ "checks" ] = JsonSerializer.SerializeToNode( new[] { new { command = "CPR manual Git snapshot checks", status = "passed", details = "Recorded history retained; latest target integrated; conflicts resolved; whitespace and sensitive-filename checks passed." } } );
				tempResult[ "limitations" ] = new JsonArray( "Manual changes did not receive another full automated review." );
				Storage.Write( Path.Combine( tempRun, "result.json" ), tempResult );
				Storage.WriteText( Path.Combine( tempRun, "report.md" ), tempSummary.PrComment + "\n\nProposed commit: " + tempSummary.CommitMessage );
				Storage.Update( Path.Combine( tempRun, "manifest.json" ), x => x[ "reviewed_tree" ] = tempTree );
				Storage.WriteText( Path.Combine( tWorkspace.Job, "base-sha" ), tempHead );
				Storage.WriteText( Path.Combine( tWorkspace.Job, "target-sha" ), tPlan.TargetSha );
				Storage.WriteText( Path.Combine( tWorkspace.Job, "target-branch" ), tWorkspace.Request.TargetBranch );
				WorkerSnapshot tempSnapshot = new( tempTree, tempSession.Length > 0 ? tempSession : null, tPlan.TargetSha, Path.GetFileName( tempRun ), false );
				await tWorkspace.SnapshotAsync( tempSnapshot, tempRun, tCancel );
				tempCode = 0;

				return tempSnapshot;
			}
			finally
			{
				if ( File.Exists( Path.Combine( tempAttempt, "attempt.json" ) ) )
				{
					Storage.Update( Path.Combine( tempAttempt, "attempt.json" ), x => { x[ "completed_at" ] = DateTimeOffset.UtcNow; x[ "exit_code" ] = tempCode; x[ "status" ] = tempCode == 0 ? "complete" : "failed"; x[ "usage" ] = JsonSerializer.SerializeToNode( Usage.Read( Path.Combine( tempAttempt, "events.jsonl" ), tempCode ), Storage.ReportOptions ); } );
				}
				Storage.Update( Path.Combine( tempRun, "manifest.json" ), x => { x[ "completed_at" ] = DateTimeOffset.UtcNow; x[ "exit_code" ] = tempCode; x[ "status" ] = tempCode == 0 ? "complete" : "failed"; } );
			}
		}
	}
}
