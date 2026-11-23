using CPR.Worker.Execution;
using CPR.Worker.Review;

namespace CPR.Worker.Reports
{
	public static class ReportValidator
	{
		public static readonly string[] CoverageAreas = [ "cleanup", "correctness", "final_diff", "resources" ];
		public static readonly string[] ScopeRules = [ "comments", "memory", "naming", "optimization", "organization", "spacing" ];

		public static bool IsValidCommitMessage( string? tMessage )
		{
			if ( tMessage is null || tMessage.Length > 4000 || Regex.IsMatch( tMessage, "[\\u0000-\\u0009\\u000b-\\u001f\\u007f]" ) )
			{
				return false;
			}
			string[] tempLines = tMessage.Split( '\n' );

			return tempLines[ 0 ].Length <= 72 && Regex.IsMatch( tempLines[ 0 ], @"^Auto: \S(?:.*\S)?$" ) && !Regex.IsMatch( tempLines[ 0 ], @"^Auto: CPR review\b", RegexOptions.IgnoreCase ) && ( tempLines.Length == 1 || tempLines[ 1 ] == "" );
		}

		public static bool IsValidComment( string? tText, int tLimit = 12000, bool tIsRequired = false )
		{
			return tText is not null && tText.Length <= tLimit && ( !tIsRequired || !string.IsNullOrWhiteSpace( tText ) ) && !tText.Contains( "<!-- cpr-report:", StringComparison.Ordinal ) && tText.All( x => x is '\t' or '\n' || ( x >= 32 && ( x < 127 || x > 159 ) ) );
		}

		public static bool IsHandoff( ReviewResult tResult, bool tIsFinal )
		{
			return !tIsFinal && tResult.Outcome == "needs_followup" && tResult.FailureKind == "unfinished_review";
		}

		public static List<string> Validate( ReviewResult tResult, bool tIsFinal, bool tIsCommitRequired, bool tIsApproved )
		{
			List<string> tempErrors = [];
			void Require( bool tCondition, string tMessage )
			{
				if ( !tCondition )
				{
					tempErrors.Add( tMessage );
				}
			}

			if ( IsHandoff( tResult, tIsFinal ) )
			{
				Require( !string.IsNullOrWhiteSpace( tResult.FollowUp ), "An intermediate review handoff needs concrete follow-up work" );
				Require( !string.IsNullOrWhiteSpace( tResult.ScopeAudit ), "An intermediate review handoff needs a scope audit" );
			}
			else
			{
				Require( tResult.Outcome == "complete", "outcome must be complete after the assigned work is done" );
				Require( tResult.FailureKind == "none", "failure_kind must be none after the assigned work is done" );
			}
			Require( !string.IsNullOrWhiteSpace( tResult.Summary ), "summary must explain the review result" );
			Require( IsValidComment( tResult.PrComment, tIsRequired: tIsFinal ), "pr_comment must contain valid publication text, at most 12000 characters; final snapshots require a comment" );
			Require( ( tResult.CommitMessage == "" && !( tIsFinal && tIsCommitRequired ) ) || IsValidCommitMessage( tResult.CommitMessage ), "commit_message needs a descriptive Auto: subject of at most 72 characters, a blank line before its body, and at most 4000 characters" );
			Require( tResult.Relevance.Status is "relevant" or "overlap", "Unresolved relevance blocks completion" );
			Require( !string.IsNullOrWhiteSpace( tResult.Relevance.Details ) && !string.IsNullOrWhiteSpace( tResult.Relevance.Evidence ), "Relevance requires a decision and supporting evidence" );
			Require( tResult.Checks.Select( x => x.Id ).Distinct( StringComparer.Ordinal ).Count() == tResult.Checks.Length, "Checks must have unique IDs" );
			for ( int tempIndex = 0; tempIndex < tResult.Checks.Length; ++tempIndex )
			{
				ReviewCheck tempCheck = tResult.Checks[ tempIndex ];
				string tempLabel = $"Check '{tempCheck.Id}'";
				Require( !string.IsNullOrWhiteSpace( tempCheck.Id ) && !string.IsNullOrWhiteSpace( tempCheck.Details ), tempLabel + " needs an ID and result explanation" );
				Require( tempCheck.Kind is "command" or "inspection", tempLabel + " kind must be command or inspection" );
				Require( tempCheck.Kind != "command" || !string.IsNullOrWhiteSpace( tempCheck.Command ), tempLabel + " command checks need the actual executed command" );
				Require( tempCheck.Status is "passed" or "failed" or "blocked" or "not_run", tempLabel + " has an invalid status" );
				if ( tempCheck.Status == "passed" )
				{
					Require( tempCheck.Kind == "inspection" ? tempCheck.ExitCode is null : tempCheck.ExitCode == 0, tempLabel + " passed commands require exit 0; non-process inspections require null" );
					Require( !string.IsNullOrWhiteSpace( tempCheck.Artifact ), tempLabel + " needs retained passing evidence" );
				}
				else if ( tempCheck.Status == "failed" )
				{
					Require( tempCheck.Kind == "inspection" ? tempCheck.ExitCode is null && !string.IsNullOrWhiteSpace( tempCheck.Artifact ) : tempCheck.ExitCode is not null and not 0, tempLabel + " failed commands require a nonzero exit code; failed inspections require retained evidence and a null exit code" );
				}
				else if ( tempCheck.Status == "blocked" )
				{
					Require( tempCheck.ExitCode is null || ( tempCheck.ExitCode != 0 && !string.IsNullOrWhiteSpace( tempCheck.Artifact ) ), tempLabel + " interrupted checks require retained output with a nonzero exit code, otherwise null" );
				}
				else
				{
					Require( tempCheck.ExitCode is null, tempLabel + " unrun checks require a null exit code" );
				}
				bool tempIsSuperseded = tempCheck.SupersededBy is not null && tResult.Checks.Skip( tempIndex + 1 ).Any( x => x.Id == tempCheck.SupersededBy && x.Status == "passed" && x.IsRequired && x.Kind == "command" && x.ExitCode == 0 && SameCommand( tempCheck.Command, x.Command ) );
				Require( tempCheck.SupersededBy is null || tempCheck.Kind == "command" && tempCheck.Status == "failed" && tempCheck.ExitCode is not null and not 0 && !string.IsNullOrWhiteSpace( tempCheck.Artifact ) && tempIsSuperseded, tempLabel + " supersession needs a later passing required invocation of the same command" );
				Require( !tempCheck.IsRequired || tempCheck.Status == "passed" || tempIsSuperseded || ( !tIsApproved && tempCheck.Status == "failed" && tempCheck.IsBaselineFailure && !string.IsNullOrWhiteSpace( tempCheck.Artifact ) ), tempLabel + ( tIsApproved ? " must pass before approved integration can publish" : " must pass or retain evidence of a demonstrated baseline failure" ) );
			}
			Require( tResult.Coverage.Keys.Order( StringComparer.Ordinal ).SequenceEqual( CoverageAreas ), "Coverage must contain correctness, resources, cleanup, and final_diff" );
			foreach ( KeyValuePair<string, Assessment> tempPair in tResult.Coverage )
			{
				tempErrors.AddRange( ValidateAssessment( tempPair.Value, "coverage." + tempPair.Key, tResult.Checks, !tIsFinal ) );
			}
			if ( tIsFinal )
			{
				Require( !string.IsNullOrWhiteSpace( tResult.ScopeAudit ), "Final validation needs a scope audit covering every required path" );
				Require( tResult.Coverage.GetValueOrDefault( "final_diff" )?.Status == "verified", "Final diff coverage must be verified with retained evidence" );
				Require( !tResult.IsResourceMeasurementRequired || tResult.Coverage.GetValueOrDefault( "resources" )?.Status == "verified", "Resource measurements must be verified when required" );
			}

			return tempErrors;
		}

		private static IEnumerable<string> ValidateAssessment( Assessment tAssessment, string tContext, ReviewCheck[] tChecks, bool tIsIncompleteAllowed )
		{
			if ( !( tAssessment.Status is "verified" or "inspected" or "not_applicable" || ( tIsIncompleteAllowed && tAssessment.Status == "incomplete" ) ) )
			{
				yield return tContext + " is incomplete or has an invalid status";
			}
			if ( string.IsNullOrWhiteSpace( tAssessment.Details ) )
			{
				yield return tContext + " must explain its assessment";
			}
			if ( tAssessment.Status == "verified" && tAssessment.Checks.Count == 0 )
			{
				yield return tContext + " needs retained passing evidence to be verified";
			}
			foreach ( string tempId in tAssessment.Checks )
			{
				if ( string.IsNullOrWhiteSpace( tempId ) || !tChecks.Any( x => x.Id == tempId && ( tAssessment.Status != "verified" || x.Status == "passed" ) ) )
				{
					yield return $"{tContext} references unknown or nonpassing check '{tempId}'";
				}
			}
		}

		public static void ValidateScope( ScopeAudit tAudit, ReviewScope tScope, ReviewResult tResult, bool tIsIncompleteAllowed = false )
		{
			WorkerFailure.Require( tAudit.ComparisonBaseSha == tScope.ComparisonBaseSha && tAudit.HeadSha == tScope.HeadSha && tAudit.TargetSha == tScope.TargetSha, "Scope audit belongs to different PR revisions", "report_metadata" );
			WorkerFailure.Require( tAudit.Files.All( x => !string.IsNullOrWhiteSpace( x.Path ) && !string.IsNullOrWhiteSpace( x.Scope ) ), "Each file needs a path and complete affected function/type scope", "unfinished_review" );
			WorkerFailure.Require( tAudit.Exclusions.All( x => x.Paths.Length > 0 && x.Paths.All( y => !string.IsNullOrWhiteSpace( y ) ) && !string.IsNullOrWhiteSpace( x.Reason ) ), "Each exclusion needs explicit paths and a concrete reason", "report_metadata" );
			string[] tempCovered = [ .. tAudit.Files.Select( x => x.Path ), .. tAudit.Exclusions.SelectMany( x => x.Paths ) ];
			WorkerFailure.Require( tempCovered.Distinct( StringComparer.Ordinal ).Count() == tempCovered.Length, "Scope audit covers a path more than once", "report_metadata" );
			WorkerFailure.Require( tempCovered.Order( StringComparer.Ordinal ).SequenceEqual( tScope.RequiredPaths.Order( StringComparer.Ordinal ) ), "Scope audit must cover exactly the required paths: " + JsonSerializer.Serialize( tScope.RequiredPaths ), "unfinished_review" );
			foreach ( ScopeFile tempFile in tAudit.Files )
			{
				WorkerFailure.Require( tempFile.Rules.Keys.Order( StringComparer.Ordinal ).SequenceEqual( ScopeRules ), tempFile.Path + " must assess naming, spacing, comments, organization, optimization, and memory", "unfinished_review" );
				foreach ( KeyValuePair<string, Assessment> tempRule in tempFile.Rules )
				{
					string[] tempErrors = [ .. ValidateAssessment( tempRule.Value, tempFile.Path + ": " + tempRule.Key, tResult.Checks, tIsIncompleteAllowed ) ];
					WorkerFailure.Require( tempErrors.Length == 0, string.Join( "\n", tempErrors ), "unfinished_review" );
				}
			}
		}

		public static void ValidateFeedback( FeedbackDisposition[] tItems, FeedbackComment[] tFeedback, ReviewResult tResult )
		{
			WorkerFailure.Require( tItems.Length == tFeedback.Length && tItems.Select( x => x.Key ).Distinct( StringComparer.Ordinal ).Count() == tItems.Length && tFeedback.All( x => tItems.Any( y => y.Key == x.Key && y.Version == x.Version ) ), "Every queued CodeRabbit version needs exactly one disposition", "unfinished_review" );
			foreach ( FeedbackDisposition tempItem in tItems )
			{
				WorkerFailure.Require( !string.IsNullOrWhiteSpace( tempItem.Reason ) && tempItem.Status is "fixed" or "already_fixed" or "not_applicable", "Unresolved CodeRabbit feedback must remain blocked", "unfinished_review" );
				string[] tempInvalid = [ .. tempItem.Checks.Where( x => !tResult.Checks.Any( y => y.Id == x && y.Status == "passed" ) ) ];
				WorkerFailure.Require( tempInvalid.Length == 0, $"Feedback '{tempItem.Key}' references unknown or nonpassing checks: {string.Join( ", ", tempInvalid )}", "missing_evidence" );
				WorkerFailure.Require( tempItem.Status != "fixed" || tempItem.Checks.Length > 0, $"Fixed feedback '{tempItem.Key}' needs retained passing evidence", "missing_evidence" );
			}
		}

		public static string[] Normalize( ReviewResult tResult, ScopeAudit? tAudit, ReviewScope? tScope )
		{
			if ( tResult.ScopeAudit != "evidence/scope_audit.json" && tResult.ScopeAudit.Contains( "evidence/scope_audit.json", StringComparison.Ordinal ) )
			{
				tResult.ScopeAudit = "evidence/scope_audit.json";
			}
			foreach ( ReviewCheck tempCheck in tResult.Checks.Where( x => x.Kind == "inspection" && x.Status == "passed" && string.IsNullOrWhiteSpace( x.Command ) && x.ExitCode == 0 && !string.IsNullOrWhiteSpace( x.Artifact ) ) )
			{
				tempCheck.ExitCode = null;
			}
			foreach ( ReviewCheck tempCheck in tResult.Checks.Where( x => x.Kind == "inspection" && x.Status == "passed" && x.ExitCode == 0 && !string.IsNullOrWhiteSpace( x.Artifact ) && Regex.IsMatch( x.Command, @"^git diff(?:\s|$)" ) ) )
			{
				tempCheck.Kind = "command";
			}
			IEnumerable<Assessment> tempAssessments = tResult.Coverage.Values.Concat( tAudit?.Files.SelectMany( x => x.Rules.Values ) ?? [] );
			foreach ( Assessment tempAssessment in tempAssessments )
			{
				for ( int tempIndex = 0; tempIndex < tempAssessment.Checks.Count; ++tempIndex )
				{
					string tempId = tempAssessment.Checks[ tempIndex ];
					if ( !tempId.EndsWith( "-final", StringComparison.Ordinal ) || tResult.Checks.Any( x => x.Id == tempId ) )
					{
						continue;
					}
					string tempStem = tempId[ ..^6 ];
					ReviewCheck[] tempMatches = [ .. tResult.Checks.Where( x => x.Id == tempStem && x.Status == "passed" && !string.IsNullOrWhiteSpace( x.Artifact ) && ( x.Command.Contains( tempStem, StringComparison.Ordinal ) || x.Artifact.Contains( tempStem, StringComparison.Ordinal ) ) ) ];
					if ( tempMatches.Length == 1 )
					{
						tempAssessment.Checks[ tempIndex ] = tempStem;
					}
				}
				if ( tempAssessment.Status == "verified" && tResult.Checks.Any( x => x.Status == "passed" && tempAssessment.Checks.Contains( x.Id ) ) )
				{
					tempAssessment.Checks.RemoveAll( x => tResult.Checks.Any( y => y.Id == x && y.Status == "failed" && y.IsBaselineFailure ) );
				}
			}
			foreach ( ReviewCheck tempCheck in tResult.Checks.Where( x => x.Status == "passed" && x.ExitCode is null && Regex.IsMatch( x.Command, @"^view_image\s" ) ) )
			{
				tempCheck.Kind = "inspection";
			}
			for ( int tempIndex = 0; tempIndex < tResult.Checks.Length; ++tempIndex )
			{
				ReviewCheck tempCheck = tResult.Checks[ tempIndex ];
				if ( tempCheck.IsRequired && tempCheck.Kind == "command" && tempCheck.Status == "failed" && tempCheck.SupersededBy is null && !string.IsNullOrWhiteSpace( tempCheck.Artifact ) )
				{
					ReviewCheck? tempSuccessor = tResult.Checks.Skip( tempIndex + 1 ).FirstOrDefault( x => x.IsRequired && x.Kind == "command" && x.Status == "passed" && x.ExitCode == 0 && !string.IsNullOrWhiteSpace( x.Artifact ) && SameCommand( tempCheck.Command, x.Command ) );
						tempCheck.SupersededBy = tempSuccessor?.Id;
				}
			}
			foreach ( Assessment tempAssessment in tempAssessments )
			{
				if ( tempAssessment.Status == "verified" )
				{
					tempAssessment.Checks.RemoveAll( x => tResult.Checks.Any( y => y.Id == x && y.Status == "failed" && y.SupersededBy is not null && tempAssessment.Checks.Contains( y.SupersededBy ) ) );
				}
			}
			if ( tAudit is null || tScope is null || tAudit.ComparisonBaseSha != tScope.ComparisonBaseSha || tAudit.HeadSha != tScope.HeadSha || tAudit.TargetSha != tScope.TargetSha )
			{
				return [];
			}
			HashSet<string> tempRequired = [ .. tScope.RequiredPaths ];
			string[] tempIncluded = [ .. tAudit.Files.Select( x => x.Path ), .. tAudit.Exclusions.SelectMany( x => x.Paths ).Where( tempRequired.Contains ) ];
			if ( tempIncluded.Length != tempRequired.Count || tempIncluded.Distinct( StringComparer.Ordinal ).Count() != tempRequired.Count || tempIncluded.Any( x => !tempRequired.Contains( x ) ) || tAudit.Files.Any( x => !tempRequired.Contains( x.Path ) ) )
			{
				return [];
			}
			string[] tempRemoved = [ .. tAudit.Exclusions.SelectMany( x => x.Paths ).Where( x => !tempRequired.Contains( x ) ) ];
			if ( tempRemoved.Length > 0 )
			{
				foreach ( ScopeExclusion tempExclusion in tAudit.Exclusions )
				{
					tempExclusion.Paths = [ .. tempExclusion.Paths.Where( tempRequired.Contains ) ];
				}
				tAudit.Exclusions = [ .. tAudit.Exclusions.Where( x => x.Paths.Length > 0 ) ];
			}

			return tempRemoved;
		}

		private static bool SameCommand( string tBefore, string tAfter )
		{
			static string NormalizeCommand( string tCommand ) => Regex.Replace( tCommand.Trim(), @"^CARGO_TARGET_DIR=\S+\s+", "" );

			return !string.IsNullOrWhiteSpace( tBefore ) && NormalizeCommand( tBefore ) == NormalizeCommand( tAfter );
		}
	}
}
