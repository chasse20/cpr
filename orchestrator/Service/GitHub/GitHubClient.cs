using CPR.Service.Command;

namespace CPR.Service.GitHub
{
	public sealed class GitHubClient( ExternalCommand tCommand )
	{
		private static readonly Regex _url = new( @"^https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/pull/(?<num>[1-9][0-9]*)(?:/)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant );

		public static (string Repository, int Number) Parse( string tUrl )
		{
			Match tempMatch = _url.Match( tUrl.Trim() );

			if ( !tempMatch.Success || tempMatch.Groups[ "owner" ].Value.Contains( ".." ) || tempMatch.Groups[ "repo" ].Value.Contains( ".." ) )
			{
				throw new ArgumentException( "Expected a full github.com/owner/repo/pull/number URL" );
			}

			return ($"{tempMatch.Groups[ "owner" ].Value}/{tempMatch.Groups[ "repo" ].Value}".ToLowerInvariant(), int.Parse( tempMatch.Groups[ "num" ].Value, CultureInfo.InvariantCulture ));
		}

		public async Task<GitHubPullRequest> ReadAsync( string tUrl, CancellationToken tCancel )
		{
			(string tempRepository, int tempNumber) = Parse( tUrl );
			CommandResult tempResult = await tCommand.RunAsync( "gh", [ "pr", "view", tUrl, "--json", "state,number,baseRefName,baseRefOid,headRefName,headRefOid,headRepository,isCrossRepository,mergeable,mergeStateStatus,isDraft,mergedAt,closedAt" ], tTimeoutSeconds: 45, tCancel: tCancel );

			if ( !tempResult.IsSuccess )
			{
				throw new InvalidOperationException( $"GitHub PR lookup failed: {tempResult.Failure}" );
			}

			using JsonDocument tempDocument = JsonDocument.Parse( tempResult.Output );
			JsonElement tempRoot = tempDocument.RootElement;
			string tempHeadRepository = tempRoot.TryGetProperty( "headRepository", out JsonElement tempHead ) && tempHead.ValueKind == JsonValueKind.Object && tempHead.TryGetProperty( "nameWithOwner", out JsonElement tempFull ) ? tempFull.GetString() ?? tempRepository : tempRepository;
			string tempState = Get( tempRoot, "state" );
			DateTimeOffset? tempCompletedAt = tempRoot.TryGetProperty( tempState == "MERGED" ? "mergedAt" : "closedAt", out JsonElement tempDate ) && tempDate.ValueKind == JsonValueKind.String && tempDate.TryGetDateTimeOffset( out DateTimeOffset tempTime ) ? tempTime : null;

			return new( tempRepository, tempNumber, Get( tempRoot, "baseRefName" ), tempHeadRepository.ToLowerInvariant(), Get( tempRoot, "headRefName" ), Get( tempRoot, "headRefOid" ), tempState, tempRoot.TryGetProperty( "isDraft", out JsonElement tempDraft ) && tempDraft.ValueKind == JsonValueKind.True, Get( tempRoot, "mergeable" ), Get( tempRoot, "mergeStateStatus" ), tempRoot.TryGetProperty( "isCrossRepository", out JsonElement tempCross ) && tempCross.ValueKind == JsonValueKind.True, tempCompletedAt, Get( tempRoot, "baseRefOid" ) );
		}

		public async Task<JsonElement> ReadReviewContextAsync( string tUrl, CancellationToken tCancel )
		{
			(string tempRepository, int tempNumber) = Parse( tUrl );
			string tempRoot = $"repos/{tempRepository}";

			Task<JsonElement>[] tempTasks =
			[
				ReadJsonAsync( [ "api", $"{tempRoot}/pulls/{tempNumber}" ], tCancel ),
				ReadJsonAsync( [ "api", "--paginate", "--slurp", $"{tempRoot}/issues/{tempNumber}/comments?per_page=100" ], tCancel ),
				ReadJsonAsync( [ "api", "--paginate", "--slurp", $"{tempRoot}/pulls/{tempNumber}/comments?per_page=100" ], tCancel ),
				ReadJsonAsync( [ "api", "--paginate", "--slurp", $"{tempRoot}/pulls/{tempNumber}/reviews?per_page=100" ], tCancel ),
				ReadJsonAsync( [ "pr", "view", tUrl, "--json", "closingIssuesReferences" ], tCancel )
			];
			JsonElement[] tempParts = await Task.WhenAll( tempTasks );
			string tempTarget = tempParts[ 0 ].GetProperty( "base" ).GetProperty( "ref" ).GetString() ?? "";
			JsonElement tempRelated = await ReadJsonAsync( [ "pr", "list", "--repo", tempRepository, "--base", tempTarget, "--state", "all", "--search", "sort:updated-desc", "--limit", "50", "--json", "number,title,body,url,state,mergedAt,updatedAt,headRefOid,files" ], tCancel );
			List<object> tempIssues = [];

			foreach ( JsonElement tempIssue in tempParts[ 4 ].GetProperty( "closingIssuesReferences" ).EnumerateArray() )
			{
				string tempUrl = tempIssue.GetProperty( "url" ).GetString() ?? "";
				Match tempMatch = Regex.Match( tempUrl, @"^https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/issues/([1-9][0-9]*)$" );

				if ( !tempMatch.Success )
				{
					throw new InvalidOperationException( "GitHub returned an unsupported linked issue URL" );
				}

				string tempPath = $"repos/{tempMatch.Groups[ 1 ].Value}/{tempMatch.Groups[ 2 ].Value}/issues/{tempMatch.Groups[ 3 ].Value}";

				Task<JsonElement> tempIssueTask = ReadJsonAsync( [ "api", tempPath ], tCancel );
				Task<JsonElement> tempCommentsTask = ReadJsonAsync( [ "api", "--paginate", "--slurp", $"{tempPath}/comments?per_page=100" ], tCancel );
				await Task.WhenAll( tempIssueTask, tempCommentsTask );
				tempIssues.Add( new { Issue = await tempIssueTask, Comments = await tempCommentsTask } );
			}

			return JsonSerializer.SerializeToElement( new { PullRequest = tempParts[ 0 ], Discussion = tempParts[ 1 ], ReviewComments = tempParts[ 2 ], Reviews = tempParts[ 3 ], LinkedIssues = tempIssues, RelatedPullRequests = tempRelated, RelatedPullRequestLimit = 50, CapturedAt = DateTimeOffset.UtcNow } );
		}

		private async Task<JsonElement> ReadJsonAsync( string[] tArguments, CancellationToken tCancel )
		{
			CommandResult tempResult = await tCommand.RunAsync( "gh", tArguments, tTimeoutSeconds: 120, tCancel: tCancel );

			if ( !tempResult.IsSuccess )
			{
				throw new InvalidOperationException( $"GitHub review context unavailable: {tempResult.Failure}" );
			}

			using JsonDocument tempDocument = JsonDocument.Parse( tempResult.Output );

			return tempDocument.RootElement.Clone();
		}

		public async Task<CIResult> ReadCIAsync( string tUrl, GitHubPullRequest tExpected, bool tIsCheckRequired, CancellationToken tCancel )
		{
			CommandResult tempResult = await tCommand.RunAsync( "gh", [ "pr", "checks", tUrl, "--required", "--json", "name,bucket,state" ], tTimeoutSeconds: 45, tCancel: tCancel );
			GitHubPullRequest tempCurrent = await ReadAsync( tUrl, tCancel );

			if ( tempCurrent.State != "OPEN" || tempCurrent.HeadSha != tExpected.HeadSha || tempCurrent.TargetBranch != tExpected.TargetBranch || tempCurrent.TargetSha != tExpected.TargetSha || tempCurrent.HeadBranch != tExpected.HeadBranch || !string.Equals( tempCurrent.HeadRepository, tExpected.HeadRepository, StringComparison.OrdinalIgnoreCase ) )
			{
				throw new InvalidOperationException( "PR changed while reading CI; check again before advancing" );
			}

			// GitHub returns JSON even when checks are pending or failed
			if ( string.IsNullOrWhiteSpace( tempResult.Output ) )
			{
				if ( tempResult.IsSuccess )
				{
					return tIsCheckRequired ? CIResult.Pending : CIResult.Passed;
				}

				throw new InvalidOperationException( $"Unable to obtain required CI checks: {tempResult.Failure}" );
			}

			using JsonDocument tempDocument = JsonDocument.Parse( tempResult.Output );

			if ( tempDocument.RootElement.ValueKind != JsonValueKind.Array )
			{
				throw new InvalidOperationException( "Invalid GitHub checks response" );
			}

			string[] tempBuckets = [ .. tempDocument.RootElement.EnumerateArray().Select( x => Get( x, "bucket" ).ToLowerInvariant() ) ];

			if ( tempBuckets.Length == 0 )
			{
				return tIsCheckRequired ? CIResult.Pending : CIResult.Passed;
			}

			if ( tempBuckets.Any( x => x is "fail" or "cancel" ) )
			{
				return CIResult.Failed;
			}

			return tempBuckets.All( x => x is "pass" or "skipping" ) ? CIResult.Passed : CIResult.Pending;
		}

		private static string Get( JsonElement tElement, string tProperty )
		{
			return tElement.TryGetProperty( tProperty, out JsonElement tempValue ) && tempValue.ValueKind == JsonValueKind.String ? tempValue.GetString() ?? "" : "";
		}
	}
}
