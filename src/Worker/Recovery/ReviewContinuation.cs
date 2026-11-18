using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;
using CPR.Worker.Review;

namespace CPR.Worker.Recovery
{
	public sealed class ReviewContinuation( ReviewExecution tReview )
	{
		public string? PinTarget()
		{
			if ( !tReview.Plan.IsContinuation || tReview.Plan.RecoveryAttempt == 0 || tReview.PreviousCheckpoint.Length == 0 )
			{
				return null;
			}

			string tempSource = tReview.SourceRun( tReview.PreviousCheckpoint );
			ReviewPlan tempPlan = Storage.Read<ReviewPlan>( Path.Combine( tempSource, "plan.json" ) );
			if ( tempPlan.OperationId != tReview.Plan.OperationId )
			{
				return null;
			}

			JsonObject tempManifest = Storage.ReadObject( Path.Combine( tempSource, "manifest.json" ) );
			RequireCompatible( tempManifest, tempPlan );
			string tempTarget = tempManifest[ "target_sha" ]!.GetValue<string>();
			Workspace.RequireSha( tempTarget );
			WorkerFailure.Require( Storage.ReadText( Path.Combine( tReview.Workspace.Job, "target-sha" ) ) == tempTarget, "Saved target integration changed; use connect to reconcile it" );

			// Resume the original target and its evidence; approval integrates later target changes
			string tempContextPath = Path.Combine( tempSource, "artifacts/context.json" );
			Storage.RequireCanonical( tempContextPath );
			JsonElement tempContext = Storage.Read<JsonElement>( tempContextPath );
			WorkerFailure.Require( tempContext.GetProperty( "TargetSha" ).GetString() == tempTarget, "Saved context belongs to a different target revision" );
			Storage.Write( Path.Combine( tReview.Artifacts, "latest-context.json" ), tReview.Plan.Context );
			Storage.Write( Path.Combine( tReview.Artifacts, "context.json" ), tempContext );
			Storage.Update( tReview.Manifest, x => x[ "observed_target_sha" ] = tReview.ExpectedTarget );

			return tempTarget;
		}

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
				RequireCompatible( tempManifest, tempPlan );
				WorkerFailure.Require( tempManifest[ "target_sha" ]?.GetValue<string>() == tReview.Target, "Saved stages belong to a different target revision; use review to reassess the integrated changes" );
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

		private void RequireCompatible( JsonObject tManifest, ReviewPlan tPlan )
		{
			WorkerFailure.Require( tManifest[ "head_sha" ]?.GetValue<string>() == tReview.Head && tPlan.HeadSha == tReview.Head && tPlan.Context.GetProperty( "PullRequest" ).GetProperty( "base" ).GetProperty( "ref" ).GetString() == tReview.Workspace.Request.TargetBranch && tPlan.MaintainerResolution == tReview.Plan.MaintainerResolution && tPlan.PublishedSha == tReview.Plan.PublishedSha && tPlan.IsMergeRepair == tReview.Plan.IsMergeRepair && tPlan.IsFeedbackRepair == tReview.Plan.IsFeedbackRepair && tPlan.ApprovedTreeSha == tReview.Plan.ApprovedTreeSha && tPlan.Steps.SequenceEqual( tReview.Plan.Steps ) && tPlan.Feedback.SequenceEqual( tReview.Plan.Feedback ), "Saved stages belong to different revisions, decisions, or review settings; use review to reassess or connect to reconcile integration" );
		}

		public static bool IsCompleted( string tDirectory, string tStep )
		{
			JsonObject tempAttempt = Storage.ReadObject( Path.Combine( tDirectory, "attempt.json" ) );

			return tempAttempt[ "step" ]?.GetValue<string>() == tStep && tempAttempt[ "is_complete" ]?.GetValue<bool>() == true && tempAttempt[ "exit_code" ]?.GetValue<int>() == 0;
		}
	}
}
