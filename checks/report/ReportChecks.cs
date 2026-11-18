using CPR.Worker.Execution;
using CPR.Worker.Reports;
using CPR.Worker.Review;

namespace CPR.Checks
{
	public static class ReportChecks
	{
		public static void Run()
		{
			ReviewResult tempResult = Result();
			tempResult.Checks = [ Command( "first", "CARGO_TARGET_DIR=/tmp/build cargo clippy --workspace", "failed", 101 ), Command( "retry", "CARGO_TARGET_DIR=/state/build cargo clippy --workspace", "passed", 0 ) ];
			tempResult.Coverage[ "correctness" ].Status = "verified";
			tempResult.Coverage[ "correctness" ].Checks.AddRange( [ "first", "retry" ] );
			Check.That( ReportValidator.Validate( tempResult, false, false, false ).Any( x => x.Contains( "Check 'first' must pass" ) ), "Historical required failure initially blocks" );
			ReportValidator.Normalize( tempResult, null, null );
			Check.That( tempResult.Checks[ 0 ].Status == "failed" && tempResult.Checks[ 0 ].ExitCode == 101 && tempResult.Checks[ 0 ].IsRequired && tempResult.Checks[ 0 ].SupersededBy == "retry", "Matching later invocation supersedes without rewriting history" );
			Check.That( !tempResult.Coverage[ "correctness" ].Checks.Contains( "first" ) && ReportValidator.Validate( tempResult, false, false, false ).Count == 0, "Verified coverage cites the passing successor" );

			ReviewResult tempDifferent = Result();
			tempDifferent.Checks = [ Command( "failed", "cargo test -p engine", "failed", 1 ), Command( "passed", "cargo test -p other", "passed", 0 ) ];
			ReportValidator.Normalize( tempDifferent, null, null );
			Check.That( tempDifferent.Checks[ 0 ].SupersededBy is null && ReportValidator.Validate( tempDifferent, false, false, false ).Any( x => x.Contains( "Check 'failed' must pass" ) ), "An unrelated passing check cannot supersede a failure" );
			tempDifferent.Checks[ 0 ].SupersededBy = "passed";
			Check.That( ReportValidator.Validate( tempDifferent, false, false, false ).Any( x => x.Contains( "supersession needs" ) ), "Invalid manual supersession is rejected" );

			ReviewResult tempInspection = Result();
			tempInspection.Checks = [ Command( "passed", "cargo test", "passed", 0 ), new ReviewCheck { Id = "before", Kind = "inspection", Command = "", Status = "failed", ExitCode = null, IsRequired = false, IsBaselineFailure = false, Details = "Recorded failing behavior", Artifact = "evidence/before.log" } ];
			Check.That( ReportValidator.Validate( tempInspection, false, false, false ).Count == 0, "A failed nonprocess inspection retains evidence with no invented exit code" );
			ReviewResult tempMetadata = Result();
			tempMetadata.ScopeAudit = "Scope audit: evidence/scope_audit.json records reviewed paths";
			tempMetadata.Checks = [ new ReviewCheck { Id = "image", Kind = "inspection", Command = "", Status = "passed", ExitCode = 0, IsRequired = false, IsBaselineFailure = false, Details = "Image inspected", Artifact = "evidence/image.png" } ];
			ReportValidator.Normalize( tempMetadata, null, null );
			Check.That( tempMetadata.ScopeAudit == "evidence/scope_audit.json" && tempMetadata.Checks[ 0 ].ExitCode is null, "Saved metadata uses the existing audit and does not invent an inspection process" );
			ReviewResult tempDiffMetadata = Result();
			tempDiffMetadata.Checks = [ new ReviewCheck { Id = "target-overlap", Kind = "inspection", Command = "git diff HEAD -- Cargo.toml", Status = "passed", ExitCode = 0, IsRequired = true, IsBaselineFailure = false, Details = "Inspected incoming dependencies", Artifact = "evidence/target-overlap.txt" } ];
			ReportValidator.Normalize( tempDiffMetadata, null, null );
			Check.That( tempDiffMetadata.Checks[ 0 ].Kind == "command" && tempDiffMetadata.Checks[ 0 ].ExitCode == 0, "A retained successful git diff process is recorded as a command" );
			ReviewResult tempNamedCheck = Result();
			tempNamedCheck.Checks = [ Command( "roadmap-integration-check", "python3 evidence/roadmap-integration-check.py", "passed", 0 ) ];
			ScopeAudit tempNamedAudit = new()
			{
				ComparisonBaseSha = "base", HeadSha = "head", TargetSha = "target", Exclusions = [],
				Files = [ new ScopeFile { Path = "ROADMAP.md", Scope = "reviewed", Rules = new() { [ "naming" ] = new Assessment { Status = "inspected", Details = "Retained output confirms the roadmap", Checks = [ "roadmap-integration-check-final" ] } } } ]
			};
			ReportValidator.Normalize( tempNamedCheck, tempNamedAudit, null );
			Check.That( tempNamedAudit.Files.Single().Rules[ "naming" ].Checks.SequenceEqual( [ "roadmap-integration-check" ] ), "Stale final suffix resolves only to the exact passed check with retained evidence" );
			tempNamedAudit.Files.Single().Rules[ "naming" ].Checks = [ "unrelated-check-final" ];
			ReportValidator.Normalize( tempNamedCheck, tempNamedAudit, null );
			Check.That( tempNamedAudit.Files.Single().Rules[ "naming" ].Checks.SequenceEqual( [ "unrelated-check-final" ] ), "Unrelated unknown check stays blocked" );

			ReviewResult tempScopeResult = Result();
			tempScopeResult.Checks = [ Command( "passed", "cargo test", "passed", 0 ) ];
			ReviewScope tempScope = new() { ComparisonBaseSha = "base", HeadSha = "head", TargetSha = "target", OriginalPrPaths = [ "needed.rs" ], WorkingTreePaths = [], ScopeKind = "full_review", ConflictPaths = [], RequiredPaths = [ "needed.rs" ] };
			ScopeAudit tempAudit = Audit( [ "needed.rs", "obsolete.rs" ] );
			Check.That( ReportValidator.Normalize( tempScopeResult, tempAudit, tempScope ).SequenceEqual( [ "obsolete.rs" ] ), "Only excess exclusion paths are removed" );
			ReportValidator.ValidateScope( tempAudit, tempScope, tempScopeResult );
			Check.That( tempAudit.Exclusions.Single().Paths.SequenceEqual( [ "needed.rs" ] ), "Required exclusion path remains" );

			ScopeAudit tempMissing = Audit( [ "obsolete.rs" ] );
			Check.That( ReportValidator.Normalize( tempScopeResult, tempMissing, tempScope ).Length == 0 && tempMissing.Exclusions.Single().Paths.SequenceEqual( [ "obsolete.rs" ] ), "Missing required paths cannot be repaired by trimming exclusions" );
			bool tempRejected = false;
			try
			{
				ReportValidator.ValidateScope( tempMissing, tempScope, tempScopeResult );
			}
			catch ( WorkerFailure )
			{
				tempRejected = true;
			}
			Check.That( tempRejected, "Missing required scope still blocks" );
		}

		private static ReviewResult Result() => new()
		{
			Outcome = "complete", Summary = "Validated", CommitMessage = "", PrComment = "", ScopeAudit = "",
			Relevance = new() { Status = "relevant", Details = "Current feature", RelatedPrs = [], Evidence = "Target inspected" },
			Coverage = ReportValidator.CoverageAreas.ToDictionary( x => x, x => new Assessment { Status = "inspected", Details = "Inspected", Checks = [] } ),
			IsResourceMeasurementRequired = false, Checks = [], Findings = [], Limitations = [], FollowUp = ""
		};

		private static ReviewCheck Command( string tId, string tCommand, string tStatus, int tExit ) => new()
		{
			Id = tId, Command = tCommand, Status = tStatus, ExitCode = tExit, IsRequired = true, IsBaselineFailure = false, Details = "Retained check", Artifact = "evidence/" + tId + ".log"
		};

		private static ScopeAudit Audit( string[] tExcluded ) => new()
		{
			ComparisonBaseSha = "base", HeadSha = "head", TargetSha = "target", Files = [], Exclusions = [ new ScopeExclusion { Paths = tExcluded, Reason = "Unchanged target paths" } ]
		};
	}
}
