using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;
using CPR.Worker.Review;

namespace CPR.Worker.Recovery
{
	public sealed class ReviewContinuation( ReviewExecution tReview )
	{
		public async Task<int> ResumeAsync( CancellationToken tCancel )
		{
			string tempSource = tReview.PreviousCheckpoint;
			int tempResume = 0;
			if ( tempSource.Length > 0 && tReview.Plan.RecoveryAttempt > 0 )
			{
				tReview.SourceRun( tempSource );
				if ( Storage.Read<ReviewPlan>( Path.Combine( tempSource, "plan.json" ) ).OperationId != tReview.Plan.OperationId )
				{
					tempSource = "";
				}
			}
			if ( tReview.Plan.IsContinuation && tempSource.Length > 0 )
			{
				tReview.SourceRun( tempSource );
				JsonObject tempManifest = Storage.ReadObject( Path.Combine( tempSource, "manifest.json" ) );
				ReviewPlan tempPlan = Storage.Read<ReviewPlan>( Path.Combine( tempSource, "plan.json" ) );
				WorkerFailure.Require( tempManifest[ "head_sha" ]?.GetValue<string>() == tReview.Head && tempManifest[ "target_sha" ]?.GetValue<string>() == tReview.Target && tempPlan.MaintainerResolution == tReview.Plan.MaintainerResolution && tempPlan.PublishedSha == tReview.Plan.PublishedSha && tempPlan.Steps.SequenceEqual( tReview.Plan.Steps ) && tempPlan.Feedback.SequenceEqual( tReview.Plan.Feedback ), "Saved stages belong to different revisions, decisions, or review settings; use review to reassess or connect to reconcile integration" );
				if ( tReview.IsMergeRepair || tReview.IsFeedbackRepair )
				{
					tReview.ScopeBase = tempManifest[ "scope_base_tree" ]!.GetValue<string>();
					Workspace.RequireSha( tReview.ScopeBase );
					await tReview.Workspace.GitAsync( [ "cat-file", "-e", tReview.ScopeBase + "^{tree}" ], tCancel );
					tReview.ConflictPaths = Storage.Read<string[]>( Path.Combine( tempSource, "conflict-paths.json" ) );
					Storage.Write( Path.Combine( tReview.Run, "conflict-paths.json" ), tReview.ConflictPaths );
					if ( tReview.IsFeedbackRepair )
					{
						tReview.FeedbackPaths = Storage.Read<string[]>( Path.Combine( tempSource, "feedback-existing-paths.json" ) );
						Storage.Write( Path.Combine( tReview.Run, "feedback-existing-paths.json" ), tReview.FeedbackPaths );
					}
					Storage.Update( tReview.Manifest, x => x[ "scope_base_tree" ] = tReview.ScopeBase );
				}
				string tempEvidence = Path.Combine( tempSource, "artifacts/evidence" );
				if ( Directory.Exists( tempEvidence ) )
				{
					Storage.CopyDirectory( tempEvidence, Path.Combine( tReview.Artifacts, "evidence" ) );
				}
				string[] tempAttempts = [ .. Directory.EnumerateDirectories( tempSource ).Where( x => File.Exists( Path.Combine( x, "attempt.json" ) ) ).Order( StringComparer.Ordinal ) ];
				while ( tempResume < tReview.Plan.Steps.Length - 1 && tempAttempts.Any( x => IsCompleted( x, tReview.Plan.Steps[ tempResume ].Name ) ) )
				{
					++tempResume;
				}
				string tempCheckpoint = "";
				foreach ( string tempDirectory in tempAttempts )
				{
					Storage.RequireCanonical( tempDirectory );
					string tempKey = Path.GetFileName( tempDirectory );
					Match tempMatch = Regex.Match( tempKey, @"^([0-9]+)-[a-z][a-z0-9-]*-[1-9][0-9]*$" );
					WorkerFailure.Require( tempMatch.Success, "Invalid saved attempt directory" );
					int tempIndex = int.Parse( tempMatch.Groups[ 1 ].Value, CultureInfo.InvariantCulture );
					JsonObject tempAttempt = Storage.ReadObject( Path.Combine( tempDirectory, "attempt.json" ) );
					if ( tempAttempt[ "is_complete" ]?.GetValue<bool>() == true && tempAttempt[ "exit_code" ]?.GetValue<int>() == 0 )
					{
						tempCheckpoint = tempAttempt[ "output_tree" ]?.GetValue<string>() ?? "";
					}
					if ( tempIndex < tempResume || ( tempIndex == tempResume && tempPlan.OperationId == tReview.Plan.OperationId ) )
					{
						string tempDestination = Path.Combine( tReview.Run, tempKey );
						Directory.CreateDirectory( tempDestination );
						foreach ( string tempName in new[] { "result.json", "gate.txt", "input-scope.json", "output-scope.json" } )
						{
							if ( File.Exists( Path.Combine( tempDirectory, tempName ) ) )
							{
								File.Copy( Path.Combine( tempDirectory, tempName ), Path.Combine( tempDestination, tempName ), true );
							}
						}
						tempAttempt[ "reused_from" ] ??= tempDirectory;
						tempAttempt[ "usage" ] = JsonSerializer.SerializeToNode( new Usage( 0, 0, 0, true ), Storage.ReportOptions );
						tempAttempt.Remove( "report_recovery" );
						tempAttempt.Remove( "report_repairs" );
						tempAttempt.Remove( "saved_validation" );
						Storage.Write( Path.Combine( tempDestination, "attempt.json" ), tempAttempt );
					}
				}
				string? tempCurrent = tempManifest[ "current_attempt" ]?.GetValue<string>();
				if ( tempCheckpoint.Length == 0 && tempCurrent is not null && tempAttempts.Contains( tempCurrent ) )
				{
					JsonObject tempAttempt = Storage.ReadObject( Path.Combine( tempCurrent, "attempt.json" ) );
					tempCheckpoint = tempAttempt[ "output_tree" ]?.GetValue<string>() ?? tempAttempt[ "input_tree" ]?.GetValue<string>() ?? "";
				}
				if ( Regex.IsMatch( tempCheckpoint, "^[a-f0-9]{40}$" ) )
				{
					await tReview.Workspace.WriteGitAsync( Path.Combine( tReview.Run, "resumed-changes.patch" ), [ "diff", "--binary", tempCheckpoint, await tReview.Workspace.FingerprintAsync( tCancel ) ], tCancel );
				}
				Storage.Update( tReview.Manifest, x => { x[ "resumed_from" ] = tempSource; x[ "resumed_step" ] = tempResume; } );
			}
			Storage.WriteText( Path.Combine( tReview.Workspace.Job, "review-checkpoint" ), tReview.Run );

			return tempResume;
		}

		public static bool IsCompleted( string tDirectory, string tStep )
		{
			JsonObject tempAttempt = Storage.ReadObject( Path.Combine( tDirectory, "attempt.json" ) );

			return tempAttempt[ "step" ]?.GetValue<string>() == tStep && tempAttempt[ "is_complete" ]?.GetValue<bool>() == true && tempAttempt[ "exit_code" ]?.GetValue<int>() == 0;
		}
	}
}
