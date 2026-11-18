using CPR.Worker.Execution;
using CPR.Worker.Persistence;

namespace CPR.Worker.Git
{
	public sealed class ApprovedWorkspace( Workspace tWorkspace )
	{
		public string Prepare()
		{
			tWorkspace.RequireAvailable();
			Workspace.RequireSha( tWorkspace.Request.ApprovedCommit! );
			Workspace.RequireSha( tWorkspace.Request.ApprovedTree! );
			Workspace.RequireSha( tWorkspace.Request.ReviewedTarget! );
			Directory.CreateDirectory( tWorkspace.Job );
			string tempBundle = Path.Combine( tWorkspace.Job, "approved-" + tWorkspace.Request.ApprovedCommit + ".bundle" );
			Storage.RequireCanonical( tempBundle );

			return tempBundle;
		}

		public async Task ImportAsync( CancellationToken tCancel )
		{
			string tempBundle = Prepare();
			WorkerRequest tempRequest = tWorkspace.Request;
			WorkerFailure.Require( File.Exists( tempBundle ), "Approved Git bundle is missing; original checkout retained", "infrastructure" );
			string tempDirectory = Path.Combine( tWorkspace.Workspaces, tWorkspace.Id, "import-" + Guid.NewGuid().ToString( "N" ) );
			Storage.RequireCanonical( tempDirectory );
			Directory.CreateDirectory( tempDirectory );

			try
			{
				await tWorkspace.GitAsync( [ "init", tempDirectory ], tCancel, tempDirectory );
				await tWorkspace.GitAsync( [ "fetch", "--no-tags", tempBundle, "refs/cpr/approved" ], tCancel, tempDirectory );
				WorkerFailure.Require( await tWorkspace.GitAsync( [ "rev-parse", "FETCH_HEAD" ], tCancel, tempDirectory ) == tempRequest.ApprovedCommit, "Imported commit differs from the approved transfer" );
				await tWorkspace.GitAsync( [ "checkout", "--detach", tempRequest.ApprovedCommit! ], tCancel, tempDirectory );
				WorkerFailure.Require( await tWorkspace.GitAsync( [ "rev-parse", "HEAD^{tree}" ], tCancel, tempDirectory ) == tempRequest.ApprovedTree && await tWorkspace.FingerprintAsync( tCancel, tempDirectory ) == tempRequest.ApprovedTree, "Imported checkout differs from the approved tree" );
				await tWorkspace.GitAsync( [ "merge-base", "--is-ancestor", tempRequest.ReviewedTarget!, "HEAD" ], tCancel, tempDirectory );
				await tWorkspace.GitAsync( [ "remote", "add", "origin", $"https://github.com/{tempRequest.Repository}.git" ], tCancel, tempDirectory );
				string tempMessage = await tWorkspace.GitAsync( [ "log", "-1", "--format=%B" ], tCancel, tempDirectory );

				// Preserve any earlier local copy before installing the verified commit
				if ( Directory.Exists( tWorkspace.Stored ) )
				{
					string tempArchive = Path.Combine( tWorkspace.Archive, $"pr-{tWorkspace.Id}-{Guid.NewGuid():N}" );
					Storage.RequireCanonical( tempArchive );
					Directory.CreateDirectory( tempArchive );
					Directory.Move( tWorkspace.Stored, Path.Combine( tempArchive, "repo" ) );
					Storage.WriteText( Path.Combine( tWorkspace.Job, "previous-workspace" ), Path.Combine( tempArchive, "repo" ) );
				}

				Directory.Move( tempDirectory, tWorkspace.Stored );
				Storage.WriteText( Path.Combine( tWorkspace.Job, "base-sha" ), tempRequest.ApprovedCommit! );
				Storage.WriteText( Path.Combine( tWorkspace.Job, "tree" ), tempRequest.ApprovedTree! );
				Storage.WriteText( Path.Combine( tWorkspace.Job, "target-sha" ), tempRequest.ReviewedTarget! );
				Storage.WriteText( Path.Combine( tWorkspace.Job, "target-branch" ), tempRequest.TargetBranch );
				Storage.Write( Path.Combine( tWorkspace.Job, "commit.json" ), new { tree = tempRequest.ApprovedTree, commit_message = tempMessage } );
				foreach ( string tempName in new[] { "session-id", "latest-run", "review-checkpoint", "published", "tweak-base.json", "tweak-checks.md", "report.md" } )
				{
					File.Delete( Path.Combine( tWorkspace.Job, tempName ) );
				}

				await tWorkspace.PrepareAsync( tCancel );
				File.Delete( tempBundle );
			}
			finally
			{
				if ( Directory.Exists( tempDirectory ) )
				{
					Directory.Delete( tempDirectory, true );
				}
			}
		}
	}
}
