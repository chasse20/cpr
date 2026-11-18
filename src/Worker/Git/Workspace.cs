using System.Runtime.InteropServices;
using CPR.Worker.Execution;
using CPR.Worker.Persistence;
using CPR.Worker.Reports;

namespace CPR.Worker.Git
{
	public sealed class Workspace
	{
		public WorkerRequest Request { get; }
		public string Root { get; } = Environment.GetEnvironmentVariable( "CPR_WORKSPACE" ) ?? "/workspace/repo";
		public string State { get; } = Environment.GetEnvironmentVariable( "CPR_STATE" ) ?? "/state/cpr";
		public string Archive { get; } = Environment.GetEnvironmentVariable( "CPR_ARCHIVE" ) ?? "/workspace/archive";
		public string Workspaces { get; } = Environment.GetEnvironmentVariable( "CPR_WORKSPACES" ) ?? "/workspace/jobs";
		public string Job { get; }
		public string Stored { get; }
		public string Url => $"https://github.com/{Request.Repository}/pull/{Request.Number}";
		public string Id => Request.Id.ToString( CultureInfo.InvariantCulture );

		public Workspace( WorkerRequest tRequest )
		{
			Request = tRequest;
			WorkerFailure.Require( tRequest.Version == 1, "Worker protocol mismatch; deploy the matching orchestrator and worker image", "infrastructure" );
			WorkerFailure.Require( tRequest.Id > 0 && tRequest.Number > 0 && Regex.IsMatch( tRequest.Repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$" ) && Regex.IsMatch( tRequest.TargetBranch, @"^[A-Za-z0-9._/-]+$" ), "Invalid workspace request", "infrastructure" );
			Job = Path.Combine( State, "jobs", Id );
			Stored = Path.Combine( Workspaces, Id, "repo" );
			Storage.RequireCanonical( Job );
			Storage.RequireCanonical( Stored );
			Directory.CreateDirectory( Path.Combine( State, "jobs" ) );
		}

		public async Task<string> GitAsync( string[] tArguments, CancellationToken tCancel, string? tDirectory = null, string? tIndex = null )
		{
			CommandResult tempResult = await GitResultAsync( tArguments, tCancel, tDirectory, tIndex );
			WorkerFailure.Require( tempResult.IsSuccess, $"Git {tArguments[ 0 ]} failed: {tempResult.Error} {tempResult.Output}", "infrastructure" );

			return tempResult.Output.TrimEnd( '\r', '\n' );
		}

		public async Task WriteGitAsync( string tPath, string[] tArguments, CancellationToken tCancel )
		{
			CommandResult tempResult = await GitResultAsync( tArguments, tCancel );
			WorkerFailure.Require( tempResult.IsSuccess, $"Git {tArguments[ 0 ]} failed: {tempResult.Error}", "infrastructure" );
			Storage.WriteText( tPath, tempResult.Output );
		}

		public Task<CommandResult> GitResultAsync( string[] tArguments, CancellationToken tCancel, string? tDirectory = null, string? tIndex = null )
		{
			return Commands.RunAsync( "git", [ "-C", tDirectory ?? Root, .. tArguments ], tEnvironment: tIndex is null ? null : new() { [ "GIT_INDEX_FILE" ] = tIndex }, tCancel: tCancel );
		}

		public async Task<string> FingerprintAsync( CancellationToken tCancel, string? tDirectory = null )
		{
			Directory.CreateDirectory( Job );
			string tempIndex = Path.Combine( Job, "snapshot-index-" + Guid.NewGuid().ToString( "N" ) );
			try
			{
				await GitAsync( [ "read-tree", "HEAD" ], tCancel, tDirectory, tempIndex );
				await GitAsync( [ "add", "-A" ], tCancel, tDirectory, tempIndex );

				return await GitAsync( [ "write-tree" ], tCancel, tDirectory, tempIndex );
			}
			finally
			{
				File.Delete( tempIndex );
				File.Delete( tempIndex + ".lock" );
			}
		}

		public void RequireAvailable()
		{
			string tempPrevious = ReadActive();
			WorkerFailure.Require( tempPrevious.Length == 0 || tempPrevious == Id || File.Exists( Path.Combine( State, "released-" + tempPrevious ) ), "Worker not released; preserving its checkout" );
			string? tempLink = new DirectoryInfo( Root ).LinkTarget;
			WorkerFailure.Require( tempLink is null ? !Directory.Exists( Root ) && !File.Exists( Root ) : tempPrevious.Length > 0 && tempLink == Path.Combine( Workspaces, tempPrevious, "repo" ), "Unexpected workspace path; preserving it" );
		}

		public async Task PrepareAsync( CancellationToken tCancel )
		{
			RequireAvailable();
			bool tempIsExisting = Directory.Exists( Path.Combine( Stored, ".git" ) );
			bool tempIsPreserved = Request.IsPreserved;
			ReviewPlan? tempPlan = Request.Plan;
			bool tempIsReview = tempPlan is not null && !tempPlan.IsReportRetry;
			if ( tempPlan?.IsFeedbackRepair == true && tempIsExisting )
			{
				if ( !File.Exists( Path.Combine( Job, "published" ) ) )
				{
					tempIsPreserved = true;
				}
				else
				{
					WorkerFailure.Require( ( await GitAsync( [ "status", "--porcelain", "--untracked-files=normal" ], tCancel, Stored ) ).Length == 0, "Unpublished edits remain after publication; preserve them for manual reconciliation" );
				}
			}

			bool tempIsStale = false;
			if ( tempIsReview && tempIsExisting )
			{
				string tempTarget = Storage.ReadText( Path.Combine( Job, "target-sha" ) );
				string tempBranch = Storage.ReadText( Path.Combine( Job, "target-branch" ) );
				tempIsStale = await GitAsync( [ "rev-parse", "HEAD" ], tCancel, Stored ) != tempPlan!.HeadSha || ( tempTarget.Length > 0 && tempTarget != tempPlan.Context.GetProperty( "TargetSha" ).GetString() ) || ( tempBranch.Length > 0 && tempBranch != Request.TargetBranch );
			}
			WorkerFailure.Require( !tempIsPreserved || tempIsExisting, "Saved workspace is missing; refusing to replace it" );
			if ( ( File.Exists( Path.Combine( Job, "published" ) ) || tempIsStale ) && !tempIsPreserved && Directory.Exists( Stored ) )
			{
				WorkerFailure.Require( tempIsReview || ( await GitAsync( [ "status", "--porcelain", "--untracked-files=normal" ], tCancel, Stored ) ).Length == 0, "Published checkout has unpublished edits; preserving workspace" );
				string tempArchive = Path.Combine( Archive, $"pr-{Id}-{Guid.NewGuid():N}" );
				Directory.CreateDirectory( tempArchive );
				Directory.Move( Stored, Path.Combine( tempArchive, "repo" ) );
				Storage.WriteText( Path.Combine( Job, "previous-workspace" ), Path.Combine( tempArchive, "repo" ) );
				tempIsExisting = false;
			}
			if ( !tempIsExisting )
			{
				Directory.CreateDirectory( Path.GetDirectoryName( Stored )! );
				CommandResult tempClone = await Commands.RunAsync( "git", [ "clone", "--no-checkout", $"https://github.com/{Request.Repository}.git", Stored ], tCancel: tCancel );
				WorkerFailure.Require( tempClone.IsSuccess, tempClone.Error, "infrastructure" );
				await GitAsync( [ "fetch", "--no-tags", "origin", $"refs/pull/{Request.Number}/head" ], tCancel, Stored );
				await GitAsync( [ "checkout", "--detach", "FETCH_HEAD" ], tCancel, Stored );
			}

			string tempNewLink = Root + "-" + Guid.NewGuid().ToString( "N" );
			Directory.CreateSymbolicLink( tempNewLink, Stored );
			try
			{
				WorkerFailure.Require( Rename( tempNewLink, Root ) == 0, $"Unable to select workspace: errno {Marshal.GetLastPInvokeError()}", "infrastructure" );
			}
			finally
			{
				File.Delete( tempNewLink );
			}
			WorkerFailure.Require( !string.IsNullOrWhiteSpace( Request.GitName ) && !string.IsNullOrWhiteSpace( Request.GitEmail ), "Missing configured Git identity", "infrastructure" );
			await GitAsync( [ "config", "user.name", Request.GitName ], tCancel );
			await GitAsync( [ "config", "user.email", Request.GitEmail ], tCancel );
			Storage.WriteText( Path.Combine( State, "active-id" ), Id );
			Storage.WriteText( Path.Combine( Job, "url" ), Url );
			if ( !tempIsExisting )
			{
				Storage.WriteText( Path.Combine( Job, "base-sha" ), await GitAsync( [ "rev-parse", "HEAD" ], tCancel ) );
				foreach ( string tempName in new[] { "tree", "session-id", "target-sha", "published", "commit.json", "commit-message.txt", "tweak-base.json", "tweak-checks.md" } )
				{
					File.Delete( Path.Combine( Job, tempName ) );
				}
				Storage.WriteText( Path.Combine( Job, "target-branch" ), Request.TargetBranch );
			}
			WorkerFailure.Require( Storage.ReadText( Path.Combine( Job, "base-sha" ) ).Length > 0, "Incomplete checkout preparation; preserve workspace for inspection" );
			File.Delete( Path.Combine( State, "released-" + Id ) );
		}

		public async Task SnapshotAsync( WorkerSnapshot tSnapshot, string tRun, CancellationToken tCancel )
		{
			RequireActive();
			WorkerFailure.Require( await GitAsync( [ "rev-parse", "HEAD" ], tCancel ) == Storage.ReadText( Path.Combine( Job, "base-sha" ) ), "Unexpected pre-approval Git commit; preserving workspace" );
			await GitAsync( [ "add", "-A" ], tCancel );
			RequireSafeNames( await GitAsync( [ "diff", "--cached", "--name-only", "-z" ], tCancel ) );
			await GitAsync( [ "diff", "--cached", "--check" ], tCancel );
			string tempTree = await GitAsync( [ "write-tree" ], tCancel );
			WorkerFailure.Require( tempTree == tSnapshot.TreeSha, "Snapshot differs from the validated tree; review again" );
			JsonObject tempResult = Storage.ReadObject( Path.Combine( tRun, "result.json" ) );
			Storage.Write( Path.Combine( Job, "commit.json" ), new { tree = tempTree, commit_message = tempResult[ "commit_message" ]?.GetValue<string>() } );
			File.Copy( Path.Combine( tRun, "report.md" ), Path.Combine( Job, "report.md" ), true );
			Storage.WriteText( Path.Combine( Job, "tree" ), tempTree );
			Storage.Write( Path.Combine( tRun, "snapshot.json" ), tSnapshot );
			Park();
			File.Delete( Path.Combine( Job, "tweak-base.json" ) );
		}

		public async Task<string> CommitAsync( string tExpected, CancellationToken tCancel )
		{
			RequireActive();

			return await CommitDirectoryAsync( tExpected, Root, tCancel );
		}

		public async Task<string> CommitParkedAsync( string tExpected, CancellationToken tCancel )
		{
			WorkerFailure.Require( File.Exists( Path.Combine( State, "released-" + Id ) ) && Directory.Exists( Path.Combine( Stored, ".git" ) ), "Saved checkout is not parked; preserving its active operation" );
			Storage.RequireCanonical( Path.Combine( Stored, ".git" ) );

			return await CommitDirectoryAsync( tExpected, Stored, tCancel );
		}

		private async Task<string> CommitDirectoryAsync( string tExpected, string tDirectory, CancellationToken tCancel )
		{
			RequireSha( tExpected );
			WorkerFailure.Require( await GitAsync( [ "rev-parse", "HEAD" ], tCancel, tDirectory ) == Storage.ReadText( Path.Combine( Job, "base-sha" ) ), "Unexpected pre-approval commit; preserving workspace" );
			WorkerFailure.Require( ( await GitResultAsync( [ "diff", "--quiet" ], tCancel, tDirectory ) ).IsSuccess && ( await GitAsync( [ "ls-files", "--others", "--exclude-standard" ], tCancel, tDirectory ) ).Length == 0, "Worker has edits outside the approved snapshot; complete the session again" );
			WorkerFailure.Require( await GitAsync( [ "write-tree" ], tCancel, tDirectory ) == tExpected && Storage.ReadText( Path.Combine( Job, "tree" ) ) == tExpected, "Approved tree no longer matches the worker tree" );
			if ( await GitAsync( [ "rev-parse", "HEAD^{tree}" ], tCancel, tDirectory ) != tExpected || File.Exists( Path.Combine( tDirectory, ".git/MERGE_HEAD" ) ) )
			{
				JsonObject tempCommit = Storage.ReadObject( Path.Combine( Job, "commit.json" ) );
				string? tempMessage = tempCommit[ "commit_message" ]?.GetValue<string>();
				WorkerFailure.Require( tempCommit[ "tree" ]?.GetValue<string>() == tExpected && ReportValidator.IsValidCommitMessage( tempMessage ), "A descriptive commit message for this snapshot is required; review before approving again", "report_metadata" );
				Storage.WriteText( Path.Combine( Job, "commit-message.txt" ), tempMessage! );
				await GitAsync( [ "-c", "core.hooksPath=/dev/null", "commit", "--cleanup=verbatim", "--file", Path.Combine( Job, "commit-message.txt" ) ], tCancel, tDirectory );
			}
			string tempSha = await GitAsync( [ "rev-parse", "HEAD" ], tCancel, tDirectory );
			Storage.WriteText( Path.Combine( Job, "base-sha" ), tempSha );

			return tempSha;
		}

		public void Park()
		{
			RequireActive();
			Storage.WriteText( Path.Combine( State, "released-" + Id ), "" );
		}

		public void Detach()
		{
			string tempPrevious = ReadActive();
			WorkerFailure.Require( tempPrevious.Length == 0 || tempPrevious == Id || File.Exists( Path.Combine( State, "released-" + tempPrevious ) ), "Worker has another active job; preserving it" );
			Storage.WriteText( Path.Combine( State, "released-" + Id ), "" );
		}

		public void Cleanup()
		{
			Storage.RequireCanonical( Archive );
			string tempPrevious = ReadActive();
			string? tempLink = new DirectoryInfo( Root ).LinkTarget;
			if ( tempPrevious == Id )
			{
				WorkerFailure.Require( tempLink == Stored || ( tempLink is null && !Directory.Exists( Root ) && !File.Exists( Root ) ), "Workspace link belongs to a different job" );
				File.Delete( Root );
				File.Delete( Path.Combine( State, "active-id" ) );
			}
			else
			{
				WorkerFailure.Require( tempLink != Stored, "Workspace ownership does not match its link" );
			}
			List<string> tempPaths = [ Path.Combine( Workspaces, Id ), Job ];
			if ( Directory.Exists( Archive ) )
			{
				tempPaths.AddRange( Directory.GetDirectories( Archive, $"pr-{Id}-*" ) );
			}
			foreach ( string tempPath in tempPaths )
			{
				Storage.RequireCanonical( tempPath );
				if ( Directory.Exists( tempPath ) )
				{
					Directory.Delete( tempPath, true );
				}
			}
			File.Delete( Path.Combine( State, "released-" + Id ) );
		}

		public void RequireActive()
		{
			WorkerFailure.Require( ReadActive() == Id && new DirectoryInfo( Root ).LinkTarget == Stored && Directory.Exists( Path.Combine( Stored, ".git" ) ), "Wrong active workspace; preserving work" );
		}

		private string ReadActive()
		{
			string tempValue = Storage.ReadText( Path.Combine( State, "active-id" ) );
			WorkerFailure.Require( tempValue.Length == 0 || Regex.IsMatch( tempValue, @"^[1-9][0-9]*$" ), "Invalid active workspace ID" );

			return tempValue;
		}

		public static void RequireSha( string tValue )
		{
			WorkerFailure.Require( Regex.IsMatch( tValue, "^[a-f0-9]{40}$" ), "Invalid Git revision" );
		}

		public static void RequireSafeNames( string tNames )
		{
			WorkerFailure.Require( !tNames.Split( '\0', StringSplitOptions.RemoveEmptyEntries ).Any( x => Regex.IsMatch( x, @"(^|/)(\.env($|\.)|id_rsa$|id_ed25519$|.*\.(pem|p12|key)$)" ) ), "Potential secret file staged; refusing to mark ready" );
		}

		[DllImport( "libc", EntryPoint = "rename", SetLastError = true )]
		private static extern int Rename( string tSource, string tDestination );
	}
}
