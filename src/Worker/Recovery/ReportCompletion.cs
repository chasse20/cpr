using CPR.Worker.Execution;
using CPR.Worker.Persistence;
using CPR.Worker.Reports;
using CPR.Worker.Review;

namespace CPR.Worker.Recovery
{
	public sealed class ReportCompletion( ReviewExecution tReview )
	{
		public async Task<bool> CompleteAsync( CancellationToken tCancel )
		{
			if ( await tReview.CheckReportAsync( tCancel ) )
			{
				return true;
			}
			ReviewResult tempResult = Storage.Read<ReviewResult>( Path.Combine( tReview.Attempt, "result.json" ), true );
			if ( tempResult.Outcome != "complete" )
			{
				return false;
			}
			File.Copy( Path.Combine( tReview.Attempt, "gate.txt" ), Path.Combine( tReview.Attempt, "gate-before-repair.txt" ), true );
			if ( tempResult.Checks.Select( x => x.Id ).Distinct().Count() == tempResult.Checks.Length )
			{
				try
				{
					File.Copy( Path.Combine( tReview.Attempt, "result.json" ), Path.Combine( tReview.Attempt, "result-before-repair.json" ), true );
					ReportValidator.Normalize( tempResult, null, null );
					foreach ( string tempArtifact in tempResult.Checks.Select( x => x.Artifact ).Append( tempResult.ScopeAudit ).Where( x => x.Length > 0 ) )
					{
						Storage.Evidence( tReview.Artifacts, tempArtifact );
					}
					ScopeAudit? tempScope = tempResult.ScopeAudit.Length > 0 ? ReadScopeAudit( Storage.Evidence( tReview.Artifacts, tempResult.ScopeAudit ) ) : null;
					if ( tempScope is not null )
					{
						File.Copy( Storage.Evidence( tReview.Artifacts, tempResult.ScopeAudit ), Path.Combine( tReview.Attempt, "scope-before-repair.json" ), true );
					}
					ReviewScope tempRequiredScope = Storage.Read<ReviewScope>( Path.Combine( tReview.Attempt, "output-scope.json" ), true );
					string[] tempRemoved = ReportValidator.Normalize( tempResult, tempScope, tempRequiredScope );
					Storage.Write( Path.Combine( tReview.Attempt, "result.json" ), tempResult, true );
					if ( tempScope is not null )
					{
						Storage.Write( Storage.Evidence( tReview.Artifacts, tempResult.ScopeAudit ), tempScope, true );
					}
					Storage.Update( Path.Combine( tReview.Attempt, "attempt.json" ), x => x[ "report_normalization" ] = "Normalized report references, inspection metadata, legacy scope paths, and " + tempRemoved.Length + " out-of-scope exclusions; original report, scope audit, and check outcomes retained" );
				}
				catch ( Exception tException ) when ( tException is WorkerFailure or JsonException or IOException )
				{
					File.AppendAllText( Path.Combine( tReview.Attempt, "gate.txt" ), "\nNormalization unavailable: " + tException.Message );
				}
				if ( await tReview.CheckReportAsync( tCancel ) )
				{
					return true;
				}
			}
			if ( tReview.Plan.RecoveryStep is not null )
			{
				return await RecoverAsync( tCancel );
			}
			File.AppendAllText( Path.Combine( tReview.Attempt, "gate.txt" ), "\nAutomatic diagnosis is disabled; inspect the retained completion errors before retrying" );

			return false;
		}

		private static ScopeAudit ReadScopeAudit( string tPath )
		{
			JsonObject tempJson = Storage.ReadObject( tPath );
			if ( tempJson[ "exclusions" ] is JsonArray tempExclusions )
			{
				foreach ( JsonObject tempExclusion in tempExclusions.OfType<JsonObject>() )
				{
					if ( tempExclusion[ "paths" ] is null && tempExclusion[ "path" ] is JsonValue tempPath && tempPath.TryGetValue<string>( out string? tempValue ) && !string.IsNullOrWhiteSpace( tempValue ) )
					{
						tempExclusion.Remove( "path" );
						tempExclusion[ "paths" ] = new JsonArray( JsonValue.Create( tempValue ) );
					}
				}
			}

			return tempJson.Deserialize<ScopeAudit>( Storage.ReportOptions ) ?? throw new JsonException( "Missing scope audit" );
		}

		private async Task<bool> RecoverAsync( CancellationToken tCancel )
		{
			string tempDirectory = Path.Combine( tReview.Attempt, "report-recovery" );
			Directory.CreateDirectory( tempDirectory );
			File.Copy( Path.Combine( tReview.Attempt, "result.json" ), Path.Combine( tempDirectory, "original-result.json" ), true );
			File.Copy( Path.Combine( tReview.Attempt, "gate.txt" ), Path.Combine( tempDirectory, "original-gate.txt" ), true );
			string tempPrompt = tReview.Plan.RecoveryStep!.Prompt + $"\nDiagnose this saved report:\n- Result: {tempDirectory}/original-result.json\n- Completion errors: {tempDirectory}/original-gate.txt\n- Scope inventory: {tReview.Attempt}/output-scope.json\n- Retained artifacts: {tReview.Artifacts}\n- Original attempt: {Storage.ReadObject( Path.Combine( tReview.Attempt, "attempt.json" ) )[ "reused_from" ]}\n- Reviewed head: {tReview.Head}; target: {tReview.Target}; tree: {tReview.OutputTree}\n- The live checkout may contain later work. It is outside this recovery.";
			int tempCode = await tReview.RunAuxiliaryAsync( "report-recovery", tReview.Plan.RecoveryStep, tempPrompt, tCancel );
			if ( tempCode != 0 )
			{
				File.AppendAllText( Path.Combine( tReview.Attempt, "gate.txt" ), $"\nAutomatic report recovery did not finish (exit {tempCode}); saved work is retained. Inspect {tempDirectory}/codex.stderr." );

				return false;
			}
			try
			{
				RecoveryDiagnosis tempDiagnosis = Storage.Read<RecoveryDiagnosis>( Path.Combine( tempDirectory, "result.json" ), true );
				WorkerFailure.Require( tempDiagnosis.Category is "report_metadata" or "missing_evidence" or "unfinished_review" or "required_check" or "integration" or "infrastructure" or "maintainer_decision", "Invalid recovery category", "report_metadata" );
				WorkerFailure.Require( !string.IsNullOrWhiteSpace( tempDiagnosis.Summary ) && tempDiagnosis.Summary.Length <= 2000 && !string.IsNullOrWhiteSpace( tempDiagnosis.NextAction ) && tempDiagnosis.NextAction.Length <= 1000 && tempDiagnosis.ReferenceRepairs.Length <= 16, "Invalid recovery diagnosis", "report_metadata" );
				Storage.Update( Path.Combine( tReview.Attempt, "attempt.json" ), x => { x[ "report_recovery" ]![ "diagnosis" ] = JsonSerializer.SerializeToNode( tempDiagnosis, Storage.ReportOptions ); x[ "report_recovery" ]![ "status" ] = "blocked"; } );
				File.AppendAllText( Path.Combine( tReview.Attempt, "gate.txt" ), $"\nRecovery diagnosis [{tempDiagnosis.Category}]: {tempDiagnosis.Summary}\nNext action: {tempDiagnosis.NextAction}" );
				ReviewResult tempResult = Storage.Read<ReviewResult>( Path.Combine( tempDirectory, "original-result.json" ), true );
				string tempAuditPath = Storage.Evidence( tReview.Artifacts, tempResult.ScopeAudit );
				File.Copy( tempAuditPath, Path.Combine( tempDirectory, "original-scope.json" ), true );
				ScopeAudit tempAudit = Storage.Read<ScopeAudit>( tempAuditPath, true );
				string? tempFeedbackPath = tReview.IsFeedbackRepair ? Storage.Evidence( tReview.Artifacts, "evidence/feedback-dispositions.json" ) : null;
				FeedbackDisposition[] tempFeedback = tempFeedbackPath is null ? [] : Storage.Read<FeedbackDisposition[]>( tempFeedbackPath, true );
				if ( tempFeedbackPath is not null )
				{
					File.Copy( tempFeedbackPath, Path.Combine( tempDirectory, "original-feedback.json" ), true );
				}
				ApplyRecovery( tempResult, tempAudit, tempFeedback, tempDiagnosis );
				await tReview.RequireUnchangedAsync( tCancel );
				Storage.Write( Path.Combine( tempDirectory, "candidate-result.json" ), tempResult, true );
				Storage.Write( Path.Combine( tempDirectory, "candidate-scope.json" ), tempAudit, true );
				if ( tempFeedbackPath is not null )
				{
					Storage.Write( Path.Combine( tempDirectory, "candidate-feedback.json" ), tempFeedback, true );
				}
				bool tempIsAccepted = false;
				try
				{
					Storage.Write( Path.Combine( tReview.Attempt, "result.json" ), tempResult, true );
					Storage.Write( tempAuditPath, tempAudit, true );
					if ( tempFeedbackPath is not null )
					{
						Storage.Write( tempFeedbackPath, tempFeedback, true );
					}
					tempIsAccepted = await tReview.CheckReportAsync( tCancel, tGate: Path.Combine( tempDirectory, "candidate-gate.txt" ) );
				}
				finally
				{
					if ( !tempIsAccepted )
					{
						File.Copy( Path.Combine( tempDirectory, "original-result.json" ), Path.Combine( tReview.Attempt, "result.json" ), true );
						File.Copy( Path.Combine( tempDirectory, "original-scope.json" ), tempAuditPath, true );
						if ( tempFeedbackPath is not null )
						{
							File.Copy( Path.Combine( tempDirectory, "original-feedback.json" ), tempFeedbackPath, true );
						}
					}
				}
				if ( tempIsAccepted )
				{
					File.Copy( Path.Combine( tempDirectory, "candidate-gate.txt" ), Path.Combine( tReview.Attempt, "gate.txt" ), true );
					Storage.Update( Path.Combine( tReview.Attempt, "attempt.json" ), x => x[ "report_recovery" ]![ "status" ] = "repaired" );
				}
				else
				{
					File.AppendAllText( Path.Combine( tReview.Attempt, "gate.txt" ), "\n" + Storage.ReadText( Path.Combine( tempDirectory, "candidate-gate.txt" ) ) + "\nProposed correction failed completion gates; original report retained" );
				}

				return tempIsAccepted;
			}
			catch ( Exception tException ) when ( tException is JsonException or IOException or WorkerFailure { Category: not ( "cancelled" or "snapshot" ) } )
			{
				File.AppendAllText( Path.Combine( tReview.Attempt, "gate.txt" ), "\nRecovery rejected: " + tException.Message );

				return false;
			}
		}

		private void ApplyRecovery( ReviewResult tResult, ScopeAudit tAudit, FeedbackDisposition[] tFeedback, RecoveryDiagnosis tDiagnosis )
		{
			WorkerFailure.Require( tResult.Outcome == "complete" && tDiagnosis.Category == "report_metadata" && tDiagnosis.ReferenceRepairs.Length <= 16 && ( tDiagnosis.ReferenceRepairs.Length > 0 || tDiagnosis.ResourceRepair is not null ), "Recovery cannot complete unfinished work or unsupported corrections", "report_metadata" );
			WorkerFailure.Require( tDiagnosis.ReferenceRepairs.Select( x => ( x.Path, x.Area, x.FeedbackKey, x.FailedCheck ) ).Distinct().Count() == tDiagnosis.ReferenceRepairs.Length, "Duplicate recovery correction", "report_metadata" );
			foreach ( ReferenceRepair tempRepair in tDiagnosis.ReferenceRepairs )
			{
				ReviewCheck[] tempBefore = [ .. tResult.Checks.Where( x => x.Id == tempRepair.FailedCheck ) ];
				ReviewCheck[] tempAfter = [ .. tResult.Checks.Where( x => x.Id == tempRepair.PassedCheck ) ];
				WorkerFailure.Require( tempBefore.Length == 1 && tempAfter.Length == 1, "Recovery references unknown or duplicate checks", "report_metadata" );
				WorkerFailure.Require( tempBefore[ 0 ].Status == "failed" && !tempBefore[ 0 ].IsRequired && tempBefore[ 0 ].ExitCode is not null and not 0 && tempAfter[ 0 ].Status == "passed" && tempAfter[ 0 ].ExitCode == 0, "Only optional historical failures with retained passing successors may be removed from verified references", "report_metadata" );
				RequireExcerpt( tempBefore[ 0 ], tempRepair.BeforeExcerpt );
				RequireExcerpt( tempAfter[ 0 ], tempRepair.AfterExcerpt );
				if ( tempRepair.Area == "feedback" )
				{
					WorkerFailure.Require( tReview.IsFeedbackRepair && tempRepair.Path.Length == 0 && !string.IsNullOrWhiteSpace( tempRepair.FeedbackKey ), "Feedback recovery requires an exact feedback key and no file path", "report_metadata" );
					FeedbackDisposition[] tempItems = [ .. tFeedback.Where( x => x.Key == tempRepair.FeedbackKey ) ];
					WorkerFailure.Require( tempItems.Length == 1 && tempItems[ 0 ].Status == "fixed" && tReview.Feedback.Any( x => x.Key == tempItems[ 0 ].Key && x.Version == tempItems[ 0 ].Version ), "Feedback recovery requires an existing fixed disposition for the captured comment version", "report_metadata" );
					WorkerFailure.Require( tempItems[ 0 ].Checks.Contains( tempRepair.FailedCheck ) && tempItems[ 0 ].Checks.Contains( tempRepair.PassedCheck ), "Both checks must already support the same fixed feedback disposition", "report_metadata" );
					tempItems[ 0 ].Checks = [ .. tempItems[ 0 ].Checks.Where( x => x != tempRepair.FailedCheck ) ];
				}
				else
				{
					WorkerFailure.Require( tempRepair.FeedbackKey.Length == 0, "Coverage recovery must not identify a feedback item", "report_metadata" );
					Assessment? tempAssessment;
					if ( tempRepair.Path.Length == 0 )
					{
						tempAssessment = ReportValidator.CoverageAreas.Contains( tempRepair.Area ) ? tResult.Coverage.GetValueOrDefault( tempRepair.Area ) : null;
					}
					else
					{
						ScopeFile[] tempFiles = [ .. tAudit.Files.Where( x => x.Path == tempRepair.Path ) ];
						tempAssessment = tempFiles.Length == 1 && ReportValidator.ScopeRules.Contains( tempRepair.Area ) ? tempFiles[ 0 ].Rules.GetValueOrDefault( tempRepair.Area ) : null;
					}
					WorkerFailure.Require( tempAssessment is not null && tempAssessment.Status == "verified" && tempAssessment.Checks.Contains( tempRepair.FailedCheck ) && tempAssessment.Checks.Contains( tempRepair.PassedCheck ), "Both checks must already support the same verified assessment", "report_metadata" );
					tempAssessment!.Checks.RemoveAll( x => x == tempRepair.FailedCheck );
				}
			}
			if ( tDiagnosis.ResourceRepair is ResourceRepair tempResourceRepair )
			{
				Assessment? tempResources = tResult.Coverage.GetValueOrDefault( "resources" );
				WorkerFailure.Require( tResult.IsResourceMeasurementRequired && tempResources?.Status == "inspected", "Resource recovery requires an inspection-labeled assessment of required measurements", "report_metadata" );
				WorkerFailure.Require( !string.IsNullOrWhiteSpace( tempResourceRepair.Reason ) && tempResourceRepair.Reason.Length <= 1000 && tempResourceRepair.BeforeCheck != tempResourceRepair.AfterCheck, "Resource recovery needs distinct before/after evidence and a justification", "report_metadata" );
				ReviewCheck[] tempBefore = [ .. tResult.Checks.Where( x => x.Id == tempResourceRepair.BeforeCheck ) ];
				ReviewCheck[] tempAfter = [ .. tResult.Checks.Where( x => x.Id == tempResourceRepair.AfterCheck ) ];
				WorkerFailure.Require( tempBefore.Length == 1 && tempAfter.Length == 1, "Resource recovery references unknown or duplicate checks", "report_metadata" );
				WorkerFailure.Require( tempBefore.Concat( tempAfter ).All( x => x.Kind == "command" && x.Status == "passed" && x.ExitCode == 0 && !string.IsNullOrWhiteSpace( x.Command ) && tempResources!.Checks.Contains( x.Id ) ), "Resource recovery requires already cited passing before/after commands", "report_metadata" );
				RequireExcerpt( tempBefore[ 0 ], tempResourceRepair.BeforeExcerpt );
				RequireExcerpt( tempAfter[ 0 ], tempResourceRepair.AfterExcerpt );
				tempResources!.Status = "verified";
				tResult.Limitations.Add( "Resource report recovery verified the already reported measurements against retained before/after evidence. Inspection-only conclusions and per-file scope assessments remain unchanged; verified resource coverage does not claim every path was measured." );
			}
			tResult.Limitations.Add( "Automatic report recovery reconciled metadata using retained before/after evidence. All original check outcomes and assessment explanations remain recorded; no code review, tests, or benchmarks were rerun." );
		}

		private void RequireExcerpt( ReviewCheck tCheck, string tExcerpt )
		{
			WorkerFailure.Require( tExcerpt.Length is >= 8 and <= 1000 && File.ReadAllText( Storage.Evidence( tReview.Artifacts, tCheck.Artifact ) ).Contains( tExcerpt, StringComparison.Ordinal ), "Recovery evidence excerpt was not found in " + tCheck.Artifact, "report_metadata" );
		}
	}
}
