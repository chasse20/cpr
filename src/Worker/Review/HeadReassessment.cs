using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;

namespace CPR.Worker.Review
{
	public static class HeadReassessment
	{
		private const int MAX_PATHS = 10;
		private const int MAX_CHANGED_LINES = 300;

		public static async Task RestoreAsync( ReviewExecution tReview, string tSource, JsonObject tManifest, CancellationToken tCancel )
		{
			string tempBase = tManifest[ "scope_base_tree" ]!.GetValue<string>();
			Workspace.RequireSha( tempBase );
			await tReview.Workspace.GitAsync( [ "cat-file", "-e", tempBase + "^{tree}" ], tCancel );
			tReview.IsHeadReassessment = true;
			tReview.ScopeBase = tempBase;
			tReview.HeadReassessmentPaths = Storage.Read<string[]>( Path.Combine( tSource, "head-reassessment-paths.json" ) );
			Storage.Write( Path.Combine( tReview.Run, "head-reassessment-paths.json" ), tReview.HeadReassessmentPaths );
			Storage.Update( tReview.Manifest, x =>
			{
				x[ "review_scope" ] = "head_reassessment";
				if ( !tReview.Plan.IsReportRetry )
				{
					x[ "kind" ] = "head_reassessment";
				}
				x[ "scope_base_tree" ] = tempBase;
				x[ "prior_review_tree" ] = tManifest[ "prior_review_tree" ]?.DeepClone();
				x[ "prior_review_head" ] = tManifest[ "prior_review_head" ]?.DeepClone();
			} );
		}

		public static async Task SelectAsync( ReviewExecution tReview, CancellationToken tCancel )
		{
			ReviewReport? tempPrior = tReview.Plan.PriorReview;
			Workspace tempWorkspace = tReview.Workspace;
			string tempArchive = Storage.ReadText( Path.Combine( tempWorkspace.Job, "previous-workspace" ) );
			string? tempArchiveParent = Path.GetDirectoryName( tempArchive );

			if ( tReview.Plan.HeadReassessmentStep is null || tempPrior is null || !Regex.IsMatch( tempPrior.RunKey, "^[A-Za-z0-9_-]+$" ) || tempArchiveParent is null || Path.GetDirectoryName( tempArchiveParent ) != tempWorkspace.Archive || !Path.GetFileName( tempArchiveParent ).StartsWith( $"pr-{tempWorkspace.Id}-", StringComparison.Ordinal ) || Path.GetFileName( tempArchive ) != "repo" || !Directory.Exists( tempArchive ) )
			{
				return;
			}

			string tempRun = Path.Combine( tempWorkspace.Job, "runs", tempPrior.RunKey );
			if ( !File.Exists( Path.Combine( tempRun, "manifest.json" ) ) )
			{
				return;
			}

			tempRun = tReview.SourceRun( tempRun );
			JsonObject tempManifest = Storage.ReadObject( Path.Combine( tempRun, "manifest.json" ) );
			if ( tempManifest[ "status" ]?.GetValue<string>() != "complete" || tempManifest[ "is_historical" ]?.GetValue<bool>() == true || tempManifest[ "head_sha" ]?.GetValue<string>() != tempPrior.HeadSha || tempManifest[ "target_sha" ]?.GetValue<string>() != tempPrior.TargetSha || tempManifest[ "reviewed_tree" ]?.GetValue<string>() != tempPrior.TreeSha || tempManifest[ "maintainer_resolution" ]?.GetValue<string>() != tReview.Plan.MaintainerResolution || tempManifest[ "previous_published_sha" ]?.GetValue<string>() != tReview.Plan.PublishedSha )
			{
				return;
			}

			string tempContextRun = tempManifest[ "report_source_run" ]?.GetValue<string>() ?? tempRun;
			if ( !Directory.Exists( tempContextRun ) )
			{
				return;
			}
			tReview.SourceRun( tempContextRun );
			string tempContextPath = Path.Combine( tempContextRun, "artifacts/context.json" );
			if ( !File.Exists( tempContextPath ) )
			{
				return;
			}

			JsonNode? tempPrevious = Storage.ReadObject( tempContextPath )[ "PullRequest" ];
			JsonNode? tempCurrent = JsonSerializer.SerializeToNode( tReview.Plan.Context.GetProperty( "PullRequest" ) );
			if ( !IsSameBranch( tempPrevious?[ "head" ], tempCurrent?[ "head" ] ) || !IsSameBranch( tempPrevious?[ "base" ], tempCurrent?[ "base" ] ) )
			{
				return;
			}

			Storage.RequireCanonical( tempArchive );
			Workspace.RequireSha( tempPrior.HeadSha );
			Workspace.RequireSha( tempPrior.TargetSha );
			Workspace.RequireSha( tempPrior.TreeSha );
			if ( !( await tempWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", tempPrior.HeadSha, tReview.Head ], tCancel ) ).IsSuccess || !( await tempWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", tempPrior.TargetSha, tReview.Target ], tCancel ) ).IsSuccess || ( await tempWorkspace.GitAsync( [ "ls-files", "-u" ], tCancel ) ).Length != 0 || await tempWorkspace.GitAsync( [ "rev-parse", "HEAD" ], tCancel, tempArchive ) != tempPrior.HeadSha || await tempWorkspace.FingerprintAsync( tCancel, tempArchive ) != tempPrior.TreeSha )
			{
				return;
			}

			string tempTree = await tempWorkspace.FingerprintAsync( tCancel );
			HashSet<string> tempPaths = new( StringComparer.Ordinal );
			Dictionary<string, string> tempEnvironment = new() { [ "GIT_ALTERNATE_OBJECT_DIRECTORIES" ] = Path.Combine( tempArchive, ".git/objects" ) };
			foreach ( (string tempBefore, string tempAfter) in new[] { (tempPrior.HeadSha, tReview.Head), (tempPrior.TargetSha, tReview.Target), (tempPrior.TreeSha, tempTree) } )
			{
				CommandResult tempDiff = await Commands.RunAsync( "git", [ "-C", tempWorkspace.Root, "diff", "--no-ext-diff", "--no-renames", "--numstat", "-z", tempBefore, tempAfter ], tEnvironment: tempEnvironment, tCancel: tCancel );
				if ( !tempDiff.IsSuccess || !AddSmallDelta( tempDiff.Output, tempPaths ) )
				{
					return;
				}
			}

			tReview.IsHeadReassessment = true;
			tReview.ScopeBase = tempTree;
			tReview.HeadReassessmentPaths = [ .. tempPaths.Order( StringComparer.Ordinal ) ];
			tReview.Session = "";
			Storage.Write( Path.Combine( tReview.Run, "head-reassessment-paths.json" ), tReview.HeadReassessmentPaths );
			Storage.Update( tReview.Manifest, x =>
			{
				x[ "kind" ] = "head_reassessment";
				x[ "scope_base_tree" ] = tempTree;
				x[ "prior_review_tree" ] = tempPrior.TreeSha;
				x[ "prior_review_head" ] = tempPrior.HeadSha;
			} );
		}

		private static bool IsSameBranch( JsonNode? tFirst, JsonNode? tSecond )
		{
			string? tempRef = tFirst?[ "ref" ]?.GetValue<string>();
			string? tempRepo = tFirst?[ "repo" ]?[ "full_name" ]?.GetValue<string>();

			return !string.IsNullOrEmpty( tempRef ) && !string.IsNullOrEmpty( tempRepo ) && tempRef == tSecond?[ "ref" ]?.GetValue<string>() && string.Equals( tempRepo, tSecond?[ "repo" ]?[ "full_name" ]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase );
		}

		private static bool AddSmallDelta( string tNumstat, HashSet<string> tPaths )
		{
			long tempLines = 0;
			foreach ( string tempEntry in tNumstat.Split( '\0', StringSplitOptions.RemoveEmptyEntries ) )
			{
				string[] tempParts = tempEntry.Split( '\t', 3 );
				if ( tempParts.Length != 3 || !int.TryParse( tempParts[ 0 ], out int tempAdded ) || !int.TryParse( tempParts[ 1 ], out int tempRemoved ) )
				{
					return false;
				}

				string tempPath = tempParts[ 2 ];
				string tempName = Path.GetFileName( tempPath );
				tempLines += (long)tempAdded + tempRemoved;
				tPaths.Add( tempPath );
				if ( tempLines > MAX_CHANGED_LINES || tPaths.Count > MAX_PATHS || tempPath.StartsWith( ".github/", StringComparison.Ordinal ) || tempPath.StartsWith( ".cargo/", StringComparison.Ordinal ) || tempName is "AGENTS.md" or "CODE_GUIDE.md" or "CODE_STYLE.md" or ".editorconfig" or ".gitmodules" or "Cargo.toml" or "Cargo.lock" or "build.rs" or "rustfmt.toml" or "package.json" or "package-lock.json" or "Dockerfile" or "Makefile" or "CMakeLists.txt" or "global.json" || Path.GetExtension( tempName ) is ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".yml" or ".yaml" )
				{
					return false;
				}
			}

			return true;
		}
	}
}
