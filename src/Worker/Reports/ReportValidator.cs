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

			Require( tResult.Outcome == "complete", "outcome must be complete after the assigned work is done" );
			Require( tResult.FailureKind == "none", "failure_kind must be none after the assigned work is done" );
			Require( !string.IsNullOrWhiteSpace( tResult.Summary ), "summary must explain the review result" );
			Require( IsValidComment( tResult.PrComment, tIsRequired: tIsFinal ), "pr_comment must contain valid publication text, at most 12000 characters; final snapshots require a comment" );
			Require( ( tResult.CommitMessage == "" && !( tIsFinal && tIsCommitRequired ) ) || IsValidCommitMessage( tResult.CommitMessage ), "commit_message needs a descriptive Auto: subject of at most 72 characters, a blank line before its body, and at most 4000 characters" );
			Require( tResult.Relevance.Status is "relevant" or "overlap", "Unresolved relevance blocks completion" );
			Require( !string.IsNullOrWhiteSpace( tResult.Relevance.Details ) && !string.IsNullOrWhiteSpace( tResult.Relevance.Evidence ), "Relevance requires a decision and supporting evidence" );
			Require( tResult.Checks.Select( x => x.Id ).Distinct( StringComparer.Ordinal ).Count() == tResult.Checks.Length, "Checks must have unique IDs" );
			foreach ( ReviewCheck tempCheck in tResult.Checks )
			{
				string tempLabel = $"Check '{tempCheck.Id}'";
				Require( !string.IsNullOrWhiteSpace( tempCheck.Id ) && !string.IsNullOrWhiteSpace( tempCheck.Command ) && !string.IsNullOrWhiteSpace( tempCheck.Details ), tempLabel + " needs an ID, actual command, and result explanation" );
				Require( tempCheck.Kind is "command" or "inspection", tempLabel + " kind must be command or inspection" );
				Require( tempCheck.Status is "passed" or "failed" or "blocked" or "not_run", tempLabel + " has an invalid status" );
				if ( tempCheck.Status == "passed" )
				{
					Require( tempCheck.Kind == "inspection" ? tempCheck.ExitCode is null : tempCheck.ExitCode == 0, tempLabel + " passed commands require exit 0; non-process inspections require null" );
					Require( !string.IsNullOrWhiteSpace( tempCheck.Artifact ), tempLabel + " needs retained passing evidence" );
				}
				else if ( tempCheck.Status == "failed" )
				{
					Require( tempCheck.ExitCode is not null and not 0, tempLabel + " failed checks require a nonzero exit code" );
				}
				else if ( tempCheck.Status == "blocked" )
				{
					Require( tempCheck.ExitCode is null || ( tempCheck.ExitCode != 0 && !string.IsNullOrWhiteSpace( tempCheck.Artifact ) ), tempLabel + " interrupted checks require retained output with a nonzero exit code, otherwise null" );
				}
				else
				{
					Require( tempCheck.ExitCode is null, tempLabel + " unrun checks require a null exit code" );
				}
				Require( !tempCheck.IsRequired || tempCheck.Status == "passed" || ( !tIsApproved && tempCheck.Status == "failed" && tempCheck.IsBaselineFailure && !string.IsNullOrWhiteSpace( tempCheck.Artifact ) ), tempLabel + ( tIsApproved ? " must pass before approved integration can publish" : " must pass or retain evidence of a demonstrated baseline failure" ) );
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

		public static void ValidateScope( ScopeAudit tAudit, ReviewScope tScope, ReviewResult tResult )
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
					string[] tempErrors = [ .. ValidateAssessment( tempRule.Value, tempFile.Path + ": " + tempRule.Key, tResult.Checks, false ) ];
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
				WorkerFailure.Require( tempItem.Checks.All( x => tResult.Checks.Any( y => y.Id == x && y.Status == "passed" ) ) && ( tempItem.Status != "fixed" || tempItem.Checks.Length > 0 ), "Fixed feedback needs retained passing evidence", "missing_evidence" );
			}
		}

		public static void Normalize( ReviewResult tResult, ScopeAudit? tScope )
		{
			IEnumerable<Assessment> tempAssessments = tResult.Coverage.Values.Concat( tScope?.Files.SelectMany( x => x.Rules.Values ) ?? [] );
			foreach ( Assessment tempAssessment in tempAssessments )
			{
				if ( tempAssessment.Status == "verified" && tResult.Checks.Any( x => x.Status == "passed" && tempAssessment.Checks.Contains( x.Id ) ) )
				{
					tempAssessment.Checks.RemoveAll( x => tResult.Checks.Any( y => y.Id == x && y.Status == "failed" && y.IsBaselineFailure ) );
				}
			}
			foreach ( ReviewCheck tempCheck in tResult.Checks.Where( x => x.Status == "passed" && x.ExitCode is null && Regex.IsMatch( x.Command, @"^view_image\s" ) ) )
			{
				tempCheck.Kind = "inspection";
			}
		}
	}
}
