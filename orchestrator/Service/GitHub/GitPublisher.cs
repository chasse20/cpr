using CPR.Service.Command;
using CPR.SQL;

namespace CPR.Service.GitHub
{
	public sealed class GitPublisher( ExternalCommand tCommand, GitHubClient tGitHub, IOptions<Settings> tSettings )
	{
		private readonly Settings _settings = tSettings.Value;

		public async Task PushAsync( PullRequest tRequest, Worker tWorker, string tNewCommit, CancellationToken tCancel )
		{
			if ( tRequest.ApprovedSha is null )
			{
				throw new InvalidOperationException( "Missing explicit approval" );
			}

			if ( !string.Equals( tRequest.Repository, tRequest.HeadRepository, StringComparison.OrdinalIgnoreCase ) )
			{
				throw new InvalidOperationException( "Fork branch publication is not supported: not authorized to push to the contributor fork" );
			}

			if ( tRequest.WorkerId != tWorker.WorkerId )
			{
				throw new InvalidOperationException( "Worker assignment no longer matches" );
			}

			if ( !Regex.IsMatch( tNewCommit, "^[a-f0-9]{40}$" ) || !Regex.IsMatch( tRequest.RemoteSha, "^[a-f0-9]{40}$" ) )
			{
				throw new InvalidOperationException( "Invalid commit SHA" );
			}

			GitHubPullRequest tempSnapshot = await tGitHub.ReadAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tCancel );

			if ( tempSnapshot.IsCrossRepository || tempSnapshot.State != "OPEN" || tempSnapshot.HeadBranch != tRequest.HeadBranch || !string.Equals( tempSnapshot.HeadRepository, tRequest.HeadRepository, StringComparison.OrdinalIgnoreCase ) || tempSnapshot.TargetBranch != tRequest.TargetBranch )
			{
				throw new InvalidOperationException( "PR destination or branch changed since review; require new approval" );
			}

			string tempDirectory = Path.Combine( _settings.GitStoragePath, tRequest.PullRequestId.ToString() );

			if ( !Directory.Exists( tempDirectory ) )
			{
				Directory.CreateDirectory( tempDirectory );
				await GitAsync( [ "init", "--bare", tempDirectory ], tCancel );
			}

			// Read the worker commit through its pinned SSH identity
			string tempSshUri = $"ssh://{tWorker.SshHost}/workspace/repo";
			await GitAsync( [ "-C", tempDirectory, "fetch", "--no-tags", tempSshUri, "HEAD" ], tCancel );
			string tempFetchedSha = await GitAsync( [ "-C", tempDirectory, "rev-parse", "FETCH_HEAD" ], tCancel );

			if ( tempFetchedSha != tNewCommit )
			{
				throw new InvalidOperationException( "Worker HEAD differs from authorized commit" );
			}

			string tempFetchedTree = await GitAsync( [ "-C", tempDirectory, "rev-parse", $"{tempFetchedSha}^{{tree}}" ], tCancel );

			if ( tempFetchedTree != tRequest.ApprovedSha )
			{
				throw new InvalidOperationException( "Published commit tree differs from approved tree" );
			}

			string tempRemoteUrl = $"https://github.com/{tRequest.Repository}.git";
			const string CREDENTIAL_HELPER = "credential.helper=!gh auth git-credential";
			string tempRemote = await GitAsync( [ "-C", tempDirectory, "-c", CREDENTIAL_HELPER, "ls-remote", tempRemoteUrl, $"refs/heads/{tRequest.HeadBranch}" ], tCancel );
			string tempRemoteHead = tempRemote.Split( '\t', StringSplitOptions.RemoveEmptyEntries ).FirstOrDefault() ?? "";

			if ( tempRemoteHead == tNewCommit )
			{
				// A prior uncertain push already succeeded
				return;
			}

			if ( tempRemoteHead != tRequest.RemoteSha || tempSnapshot.HeadSha != tRequest.RemoteSha )
			{
				throw new InvalidOperationException( "PR branch was modified by someone else; refuse stale push" );
			}

			// Verify fast-forward ancestry before publication
			await GitAsync( [ "-C", tempDirectory, "-c", CREDENTIAL_HELPER, "fetch", "--no-tags", tempRemoteUrl, $"refs/heads/{tRequest.HeadBranch}" ], tCancel );
			CommandResult tempAncestor = await tCommand.RunAsync( "git", [ "-C", tempDirectory, "merge-base", "--is-ancestor", tRequest.RemoteSha, tNewCommit ], tTimeoutSeconds: 60, tCancel: tCancel );

			if ( !tempAncestor.IsSuccess )
			{
				throw new InvalidOperationException( "Approved commit does not fast-forward the remote PR head" );
			}

			await GitAsync( [ "-C", tempDirectory, "-c", CREDENTIAL_HELPER, "push", tempRemoteUrl, $"{tNewCommit}:refs/heads/{tRequest.HeadBranch}" ], tCancel );
			string tempAfter = await GitAsync( [ "-C", tempDirectory, "-c", CREDENTIAL_HELPER, "ls-remote", tempRemoteUrl, $"refs/heads/{tRequest.HeadBranch}" ], tCancel );

			if ( !tempAfter.StartsWith( tNewCommit + "\t", StringComparison.Ordinal ) )
			{
				throw new InvalidOperationException( "Push result not confirmed; inspect remote before retry" );
			}
		}

		private async Task<string> GitAsync( IEnumerable<string> tArguments, CancellationToken tCancel )
		{
			CommandResult tempResult = await tCommand.RunAsync( "git", tArguments, tTimeoutSeconds: 180, tCancel: tCancel );

			if ( !tempResult.IsSuccess )
			{
				throw new InvalidOperationException( $"Git operation failed: {tempResult.Failure}" );
			}

			return tempResult.Output.Trim();
		}
	}
}
