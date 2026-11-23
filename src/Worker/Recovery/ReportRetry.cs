using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;
using CPR.Worker.Reports;
using CPR.Worker.Review;

namespace CPR.Worker.Recovery
{
	public sealed class ReportRetry( ReviewExecution tReview )
	{
		public async Task RunAsync( CancellationToken tCancel )
		{
			string tempSource = tReview.SourceRun( Storage.ReadObject( tReview.Manifest )[ "report_source_run" ]!.GetValue<string>() );
			JsonObject tempManifest = Storage.ReadObject( Path.Combine( tempSource, "manifest.json" ) );
			ReviewPlan tempPlan = Storage.Read<ReviewPlan>( Path.Combine( tempSource, "plan.json" ) );
			WorkerFailure.Require( tempManifest[ "status" ]?.GetValue<string>() == "failed" && tempManifest[ "exit_code" ]?.GetValue<int>() == 13 && tempManifest[ "kind" ]?.GetValue<string>() != "manual_tweak" && tempManifest[ "head_sha" ]?.GetValue<string>() == tReview.Head && tempManifest[ "target_sha" ]?.GetValue<string>() == tReview.Target && tempPlan.PublishedSha == tReview.Plan.PublishedSha && tempPlan.MaintainerResolution == tReview.Plan.MaintainerResolution, "Report recovery requires a failed completed review report with unchanged source, destination, publication, and maintainer decisions" );
			string tempAttempt = tempManifest[ "current_attempt" ]!.GetValue<string>();
			Storage.RequireCanonical( tempAttempt );
			Match tempMatch = Regex.Match( Path.GetFileName( tempAttempt ), @"^([0-9]+)-([a-z][a-z0-9-]*)-[1-9][0-9]*$" );
			WorkerFailure.Require( Path.GetDirectoryName( tempAttempt ) == tempSource && tempMatch.Success, "No canonical completed review attempt is available" );
			int tempIndex = int.Parse( tempMatch.Groups[ 1 ].Value, CultureInfo.InvariantCulture );
			WorkerFailure.Require( tempIndex < tempPlan.Steps.Length && tempPlan.Steps[ tempIndex ].Name == tempMatch.Groups[ 2 ].Value, "Saved attempt does not match its plan" );
			bool tempIsSavedValidationRequired = tempIndex == tempPlan.Steps.Length - 2 && tempPlan.Steps[ ^1 ].Name == "validation";
			WorkerFailure.Require( tempIndex == tempPlan.Steps.Length - 1 || tempIsSavedValidationRequired, "Unfinished review stages remain; report recovery cannot replace missing work" );
			JsonObject tempMetadata = Storage.ReadObject( Path.Combine( tempAttempt, "attempt.json" ) );
			ReviewResult tempResult = Storage.Read<ReviewResult>( Path.Combine( tempAttempt, "result.json" ), true );
			WorkerFailure.Require( tempMetadata[ "step" ]?.GetValue<string>() == tempPlan.Steps[ tempIndex ].Name && tempMetadata[ "exit_code" ]?.GetValue<int>() == 0 && tempMetadata[ "status" ]?.GetValue<string>() == "executed" && tempResult.Outcome == "complete", "Saved review is incomplete; retry cannot invent missing work", "unfinished_review" );
			string[] tempAttempts = [ .. Directory.GetDirectories( tempSource ).Where( x => File.Exists( Path.Combine( x, "attempt.json" ) ) ) ];
			for ( int i = 0; i < tempIndex; ++i )
			{
				WorkerFailure.Require( tempAttempts.Any( x => ReviewContinuation.IsCompleted( x, tempPlan.Steps[ i ].Name ) ), "Saved review stage was not completed: " + tempPlan.Steps[ i ].Name );
			}
			tReview.OutputTree = tempMetadata[ "output_tree" ]!.GetValue<string>();
			Workspace.RequireSha( tReview.OutputTree );
			await tReview.Workspace.GitAsync( [ "cat-file", "-e", tReview.OutputTree + "^{tree}" ], tCancel );
			tReview.WorkspaceTree = await tReview.Workspace.FingerprintAsync( tCancel );
			bool tempApprovedIntegration = tempManifest[ "kind" ]?.GetValue<string>() == "merge_repair" && tempPlan.ApprovedTreeSha is not null && tReview.WorkspaceHead != tReview.Head && await tReview.Workspace.GitAsync( [ "rev-parse", "HEAD^{tree}" ], tCancel ) == tempPlan.ApprovedTreeSha && ( await tReview.Workspace.GitResultAsync( [ "merge-base", "--is-ancestor", tReview.Head, "HEAD" ], tCancel ) ).IsSuccess;
			// A newer target is integrated after approval. It does not invalidate unchanged
			// reviewed code, its saved evidence, or the target already reviewed here.
			tReview.IsHistorical = tReview.Plan.IsHistoricalReport || tReview.WorkspaceHead != tReview.Head && !tempApprovedIntegration || tReview.WorkspaceTree != tReview.OutputTree;
			if ( !tReview.IsHistorical )
			{
				string tempMerge = Storage.ReadText( Path.Combine( tReview.Workspace.Root, ".git/MERGE_HEAD" ) );
				WorkerFailure.Require( tempMerge.Length > 0 ? tempMerge == tReview.Target : ( await tReview.Workspace.GitResultAsync( [ "merge-base", "--is-ancestor", tReview.Target, "HEAD" ], tCancel ) ).IsSuccess, "Reviewed target integration is no longer present" );
			}
			Storage.CopyDirectory( Path.Combine( tempSource, "artifacts" ), tReview.Artifacts );
			File.Copy( Path.Combine( tempSource, "plan.json" ), Path.Combine( tReview.Run, "source-plan.json" ), true );
			tReview.Session = tempMetadata[ "session_id" ]?.GetValue<string>() ?? "";
			tReview.IsFinal = true;
			tReview.IsCommitRequired = tReview.OutputTree != await tReview.Workspace.GitAsync( [ "rev-parse", tReview.Head + "^{tree}" ], tCancel ) || !( await tReview.Workspace.GitResultAsync( [ "merge-base", "--is-ancestor", tReview.Target, tReview.Head ], tCancel ) ).IsSuccess;
			string? tempKind = tempManifest[ "kind" ]?.GetValue<string>();
			if ( tempKind is "merge_repair" or "feedback_repair" )
			{
				tReview.IsMergeRepair = true;
				tReview.ScopeBase = tempManifest[ "scope_base_tree" ]!.GetValue<string>();
				Workspace.RequireSha( tReview.ScopeBase );
				tReview.ConflictPaths = Storage.Read<string[]>( Path.Combine( tempSource, "conflict-paths.json" ) );
				Storage.Write( Path.Combine( tReview.Run, "conflict-paths.json" ), tReview.ConflictPaths );
				Storage.Update( tReview.Manifest, x => { x[ "review_scope" ] = "merge_repair"; x[ "scope_base_tree" ] = tReview.ScopeBase; } );
			}
			if ( tempKind == "feedback_repair" )
			{
				tReview.IsFeedbackRepair = true;
				tReview.Feedback = Storage.Read<FeedbackComment[]>( Path.Combine( tReview.Artifacts, "feedback.json" ) );
				tReview.FeedbackPaths = Storage.Read<string[]>( Path.Combine( tempSource, "feedback-existing-paths.json" ) );
				Storage.Write( Path.Combine( tReview.Run, "feedback-existing-paths.json" ), tReview.FeedbackPaths );
				Storage.Update( tReview.Manifest, x => { x[ "review_scope" ] = "feedback_repair"; x[ "feedback" ] = tempManifest[ "feedback" ]?.DeepClone(); x[ "feedback_session_id" ] = tempManifest[ "feedback_session_id" ]?.DeepClone(); } );
			}
			tReview.Attempt = Path.Combine( tReview.Run, "0-report-retry-1" );
			Storage.Write( Path.Combine( tReview.Attempt, "attempt.json" ), new { step = "report-retry", attempt = 1, status = "reused", started_at = DateTimeOffset.UtcNow, input_tree = tReview.OutputTree, output_tree = tReview.OutputTree, session_id = tReview.Session, reused_from = tempAttempt, exit_code = 0, usage = new { input_tokens = 0, cached_input_tokens = 0, output_tokens = 0, is_complete = true } } );
			Storage.Update( tReview.Manifest, x => { x[ "attempts" ] = new JsonArray( tReview.Attempt ); x[ "current_attempt" ] = tReview.Attempt; } );
			Storage.Write( Path.Combine( tReview.Attempt, "output-scope.json" ), await tReview.ScopeAsync( tReview.OutputTree, tCancel ), true );
			if ( tempIsSavedValidationRequired )
			{
				const string NOTE = "Final validation was recovered through a read-only check of the saved review, scope audit, diff, and retained evidence. The configured independent code, optimization, and memory review passes were not rerun; later manual integration has separate coverage.";
				tempResult.Limitations.Add( NOTE );
				tempResult.PrComment += "\n\nRecovery validation: " + NOTE;
			}
			Storage.Write( Path.Combine( tReview.Attempt, "result.json" ), tempResult, true );
			bool tempIsComplete = await new ReportCompletion( tReview ).CompleteAsync( tCancel );
			if ( tempIsComplete && tempIsSavedValidationRequired )
			{
				tempIsComplete = await ValidateSavedAsync( tempPlan.Steps[ ^1 ].Name, tCancel );
			}
			Storage.Update( Path.Combine( tReview.Attempt, "attempt.json" ), x => { x[ "is_complete" ] = tempIsComplete; x[ "completed_at" ] = DateTimeOffset.UtcNow; } );
			string tempGate = Storage.ReadText( Path.Combine( tReview.Attempt, "gate.txt" ) );
			string tempFailure = tempGate.Contains( " must pass or retain evidence", StringComparison.Ordinal ) || tempGate.Contains( " must pass before approved integration", StringComparison.Ordinal ) ? "required_check" : tempGate.Contains( "Scope audit must cover exactly", StringComparison.Ordinal ) ? "unfinished_review" : "report_metadata";
			WorkerFailure.Require( tempIsComplete, "Saved report remains invalid; review work was not rerun. " + tempGate, tempFailure );
		}

		private async Task<bool> ValidateSavedAsync( string tStep, CancellationToken tCancel )
		{
			string tempDirectory = Path.Combine( tReview.Attempt, "saved-validation" );
			await tReview.Workspace.WriteGitAsync( Path.Combine( tempDirectory, "reviewed.patch" ), [ "diff", "--binary", tReview.Head, tReview.OutputTree ], tCancel );
			string tempPrompt = tReview.Plan.SavedValidationStep.Prompt + $"\nSaved evidence to validate:\n- Review result: {tReview.Attempt}/result.json\n- Required scope: {tReview.Attempt}/output-scope.json\n- Scope audit and check artifacts: {tReview.Artifacts}\n- Reviewed diff: {tempDirectory}/reviewed.patch\n- Original PR head: {tReview.Head}; original target: {tReview.Target}; reviewed tree: {tReview.OutputTree}\n- The configured final validation stage never started because the completed review's report failed a structural gate. All final report, scope, artifact, and diff gates now pass.\n- The live checkout may contain newer manual edits. Those are outside this validation; do not evaluate or alter them.";
			int tempCode = await tReview.RunAuxiliaryAsync( "saved-validation", tReview.Plan.SavedValidationStep, tempPrompt, tCancel );
			if ( tempCode != 0 )
			{
				File.AppendAllText( Path.Combine( tReview.Attempt, "gate.txt" ), "\nSaved-evidence validation did not complete; no full review was started" );

				return false;
			}
			SavedValidationResult tempResult = Storage.Read<SavedValidationResult>( Path.Combine( tempDirectory, "result.json" ), true );
			bool tempIsComplete = tempResult.IsValidated && tempResult.Issues.Length == 0 && !string.IsNullOrWhiteSpace( tempResult.Summary ) && tempResult.Summary.Length <= 2000;
			if ( tempIsComplete )
			{
				Storage.Update( Path.Combine( tReview.Attempt, "attempt.json" ), x => { x[ "saved_validation" ]![ "result" ] = JsonSerializer.SerializeToNode( tempResult, Storage.ReportOptions ); x[ "saved_validation" ]![ "status" ] = "complete"; } );
				Storage.Update( tReview.Manifest, x => { x[ "validation_mode" ] = "saved_evidence"; x[ "recovered_validation_step" ] = tStep; } );
			}
			else
			{
				File.AppendAllText( Path.Combine( tReview.Attempt, "gate.txt" ), "\n" + tempResult.Summary + "\n" + string.Join( '\n', tempResult.Issues ) );
			}

			return tempIsComplete;
		}
	}
}
