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
			string tempHeadRepository = tempRoot.TryGetProperty( "headRepository", out JsonElement tempHead ) && tempHead.ValueKind == JsonValueKind.Object && tempHead.TryGetProperty( "nameWithOwner", out JsonElement tempFull ) ? tempFull.GetString() ?? "" : "";
			string tempState = Get( tempRoot, "state" );
			string tempTarget = Get( tempRoot, "baseRefName" );
			string tempTargetSha = tempState == "OPEN" ? await ReadTargetShaAsync( tempRepository, tempTarget, tCancel ) : Get( tempRoot, "baseRefOid" );
			DateTimeOffset? tempCompletedAt = tempRoot.TryGetProperty( tempState == "MERGED" ? "mergedAt" : "closedAt", out JsonElement tempDate ) && tempDate.ValueKind == JsonValueKind.String && tempDate.TryGetDateTimeOffset( out DateTimeOffset tempTime ) ? tempTime : null;

			return new( tempRepository, tempNumber, tempTarget, tempHeadRepository.ToLowerInvariant(), Get( tempRoot, "headRefName" ), Get( tempRoot, "headRefOid" ), tempState, tempRoot.TryGetProperty( "isDraft", out JsonElement tempDraft ) && tempDraft.ValueKind == JsonValueKind.True, Get( tempRoot, "mergeable" ), Get( tempRoot, "mergeStateStatus" ), tempRoot.TryGetProperty( "isCrossRepository", out JsonElement tempCross ) && tempCross.ValueKind == JsonValueKind.True, tempCompletedAt, tempTargetSha );
		}

		public async Task VerifyPublicationAccessAsync( GitHubPullRequest tExpected, CancellationToken tCancel )
		{
			(string tempRepository, int tempNumber) = Parse( tExpected.Url );
			string[] tempParts = tempRepository.Split( '/' );
			const string QUERY = """
				query($owner: String!, $name: String!, $number: Int!) {
				  repository(owner: $owner, name: $name) {
				    viewerPermission
				    pullRequest(number: $number) {
				      state baseRefName baseRef { target { oid } }
				      headRefName headRefOid maintainerCanModify
				      headRepository { nameWithOwner viewerPermission isInOrganization }
				    }
				  }
				}
				""";
			CommandResult tempResult = await tCommand.RunAsync( "gh", [ "api", "graphql", "-f", $"query={QUERY}", "-f", $"owner={tempParts[ 0 ]}", "-f", $"name={tempParts[ 1 ]}", "-F", $"number={tempNumber.ToString( CultureInfo.InvariantCulture )}" ], tTimeoutSeconds: 45, tCancel: tCancel );

			if ( !tempResult.IsSuccess )
			{
				throw new InvalidOperationException( $"Unable to verify GitHub publication access: {tempResult.Failure}" );
			}

			using JsonDocument tempDocument = JsonDocument.Parse( tempResult.Output );
			VerifyPublicationAccess( tempDocument.RootElement, tExpected );
		}

		private static void VerifyPublicationAccess( JsonElement tRoot, GitHubPullRequest tExpected )
		{
			JsonElement tempRepository = tRoot.GetProperty( "data" ).GetProperty( "repository" );

			if ( tempRepository.ValueKind != JsonValueKind.Object || !tempRepository.TryGetProperty( "pullRequest", out JsonElement tempRequest ) || tempRequest.ValueKind != JsonValueKind.Object || !tempRequest.TryGetProperty( "headRepository", out JsonElement tempHead ) || tempHead.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace( Get( tempHead, "nameWithOwner" ) ) )
			{
				throw new InvalidOperationException( "PR source repository is unavailable; it may have been deleted or access was revoked" );
			}

			string tempTargetSha = tempRequest.TryGetProperty( "baseRef", out JsonElement tempBase ) && tempBase.ValueKind == JsonValueKind.Object && tempBase.TryGetProperty( "target", out JsonElement tempTarget ) && tempTarget.ValueKind == JsonValueKind.Object ? Get( tempTarget, "oid" ) : "";

			if ( Get( tempRequest, "state" ) != "OPEN" || Get( tempRequest, "headRefName" ) != tExpected.HeadBranch || Get( tempRequest, "headRefOid" ) != tExpected.HeadSha || Get( tempRequest, "baseRefName" ) != tExpected.TargetBranch || string.IsNullOrEmpty( tempTargetSha ) || tempTargetSha != tExpected.TargetSha || !string.Equals( Get( tempHead, "nameWithOwner" ), tExpected.HeadRepository, StringComparison.OrdinalIgnoreCase ) )
			{
				throw new InvalidOperationException( "PR head or target changed while checking publication access; request fresh approval" );
			}

			bool tempIsHeadWritable = Get( tempHead, "viewerPermission" ) is "WRITE" or "MAINTAIN" or "ADMIN";
			bool tempIsUpstreamWritable = Get( tempRepository, "viewerPermission" ) is "WRITE" or "MAINTAIN" or "ADMIN";
			bool tempIsMaintainerEditAllowed = tempRequest.TryGetProperty( "maintainerCanModify", out JsonElement tempModification ) && tempModification.ValueKind == JsonValueKind.True;
			bool tempIsPersonalFork = !string.Equals( tExpected.Repository, tExpected.HeadRepository, StringComparison.OrdinalIgnoreCase ) && tempHead.TryGetProperty( "isInOrganization", out JsonElement tempOrganization ) && tempOrganization.ValueKind == JsonValueKind.False;

			// Maintainer edits grant access to this PR branch, not write access to the whole fork
			if ( !tempIsHeadWritable && !( tempIsPersonalFork && tempIsUpstreamWritable && tempIsMaintainerEditAllowed ) )
			{
				throw new InvalidOperationException( $"Cannot publish to {tExpected.HeadRepository}:{tExpected.HeadBranch}: CPR's GitHub account needs write access to the source repository, or upstream write access with 'Allow edits from maintainers' enabled on a personal fork. Check the configured GitHub token's repository access" );
			}
		}


		private readonly Dictionary<string, FeedbackPage> _feedbackPages = [];

		public void PruneFeedbackCache( IEnumerable<(string Repository, int Number)> tRequests )
		{
			HashSet<string> tempPaths = [ .. tRequests.SelectMany( x => new[] { $"repos/{x.Repository}/issues/{x.Number}/comments?", $"repos/{x.Repository}/pulls/{x.Number}/comments?", $"repos/{x.Repository}/pulls/{x.Number}/reviews?" } ) ];

			foreach ( string tempPath in _feedbackPages.Keys.Where( x => !tempPaths.Any( x.StartsWith ) ).ToArray() )
			{
				_feedbackPages.Remove( tempPath );
			}
		}

		public async Task<FeedbackComment[]> ReadFeedbackAsync( string tRepository, int tNumber, CancellationToken tCancel )
		{
			List<FeedbackComment> tempComments = [];
			(string Kind, string Path)[] tempSources =
			[
				("discussion", $"repos/{tRepository}/issues/{tNumber}/comments"),
				("inline", $"repos/{tRepository}/pulls/{tNumber}/comments"),
				("review", $"repos/{tRepository}/pulls/{tNumber}/reviews")
			];

			foreach ( (string tempKind, string tempPath) in tempSources )
			{
				int tempPage = 1;
				bool tempIsNext = true;

				while ( tempIsNext )
				{
					FeedbackPage tempResponse = await ReadFeedbackPageAsync( $"{tempPath}?per_page=100&page={tempPage}", tCancel );
					tempIsNext = tempResponse.IsNextPage;
					++tempPage;

					foreach ( JsonElement tempItem in tempResponse.Comments )
					{
						if ( tempItem.TryGetProperty( "user", out JsonElement tempUser ) && Get( tempUser, "login" ).Equals( "coderabbitai[bot]", StringComparison.OrdinalIgnoreCase ) && Get( tempUser, "type" ) == "Bot" && !string.IsNullOrWhiteSpace( Get( tempItem, "body" ) ) && Get( tempItem, "state" ) is not ( "PENDING" or "DISMISSED" ) && IsActionableFeedback( tempKind, Get( tempItem, "body" ) ) )
						{
							string tempBody = Get( tempItem, "body" );
							string tempFile = Get( tempItem, "path" );
							string tempCommit = Get( tempItem, "commit_id" );
							// GitHub remaps inline commit IDs after a push without changing the finding
							int tempInfo = tempKind == "review" ? tempBody.IndexOf( "<h6>", StringComparison.Ordinal ) : -1;
							string tempFinding = tempInfo < 0 ? tempBody : tempBody[ ..tempInfo ].TrimEnd();
							string tempVersion = Convert.ToHexStringLower( System.Security.Cryptography.SHA256.HashData( Encoding.UTF8.GetBytes( JsonSerializer.Serialize( new[] { tempFinding, tempFile } ) ) ) );
							tempComments.Add( new( $"{tempKind}:{tempItem.GetProperty( "id" ).GetInt64()}", tempVersion, Get( tempItem, "html_url" ), tempBody, tempFile, tempCommit ) );
						}
					}
				}
			}

			return [ .. tempComments ];
		}

		private static bool IsActionableFeedback( string tKind, string tBody )
		{
			if ( Regex.IsMatch( tBody, @"✅\s+Addressed in commit [a-f0-9]+\s*\z", RegexOptions.IgnoreCase ) )
			{
				return false;
			}

			if ( tKind == "discussion" && tBody.Contains( "<!-- This is an auto-generated comment: summarize by coderabbit.ai -->", StringComparison.Ordinal ) )
			{
				return false;
			}

			// Review summaries only trigger repair when CodeRabbit reports actionable comments
			return tKind != "review" || Regex.IsMatch( tBody, @"\*\*Actionable comments posted:\s*[1-9][0-9]*\*\*" );
		}

		private async Task<FeedbackPage> ReadFeedbackPageAsync( string tPath, CancellationToken tCancel )
		{
			_feedbackPages.TryGetValue( tPath, out FeedbackPage? tempCached );
			List<string> tempArguments = [ "api", "--include", tPath ];

			if ( !string.IsNullOrEmpty( tempCached?.ETag ) )
			{
				tempArguments.AddRange( [ "-H", $"If-None-Match: {tempCached.ETag}" ] );
			}

			CommandResult tempResult = await tCommand.RunAsync( "gh", [ .. tempArguments ], tTimeoutSeconds: 45, tCancel: tCancel );
			string tempOutput = tempResult.Output.Replace( "\r\n", "\n", StringComparison.Ordinal );
			int tempBoundary = tempOutput.IndexOf( "\n\n", StringComparison.Ordinal );
			string tempHeaders = tempBoundary >= 0 ? tempOutput[ ..tempBoundary ] : tempOutput;

			if ( Regex.IsMatch( tempHeaders, @"\AHTTP/\S+ 304\b" ) && tempCached is not null )
			{
				return tempCached;
			}

			if ( !tempResult.IsSuccess || !Regex.IsMatch( tempHeaders, @"\AHTTP/\S+ 200\b" ) || tempBoundary < 0 )
			{
				throw new InvalidOperationException( $"GitHub feedback lookup failed: {tempResult.Failure}" );
			}

			using JsonDocument tempDocument = JsonDocument.Parse( tempOutput[ ( tempBoundary + 2 ).. ] );
			string tempETag = Regex.Match( tempHeaders, @"(?im)^etag:\s*([^\n]+)" ).Groups[ 1 ].Value.Trim();
			bool tempIsNext = Regex.Match( tempHeaders, @"(?im)^link:\s*([^\n]+)" ).Groups[ 1 ].Value.Contains( "rel=\"next\"", StringComparison.Ordinal );
			FeedbackPage tempPage = new( tempETag, tempIsNext, [ .. tempDocument.RootElement.EnumerateArray().Select( x => x.Clone() ) ] );
			_feedbackPages[ tPath ] = tempPage;

			return tempPage;
		}

		public async Task<(string HeadSha, bool? IsComplete)> ReadCodeRabbitStatusAsync( string tRepository, int tNumber, CancellationToken tCancel )
		{
			JsonElement tempResult = await ReadJsonAsync( [ "pr", "view", $"https://github.com/{tRepository}/pull/{tNumber}", "--json", "headRefOid,statusCheckRollup" ], tCancel );
			JsonElement[] tempChecks = [ .. tempResult.GetProperty( "statusCheckRollup" ).EnumerateArray().Where( x => ( Get( x, "name" ) + Get( x, "context" ) ).Contains( "coderabbit", StringComparison.OrdinalIgnoreCase ) ) ];
			bool? tempIsComplete = tempChecks.Length == 0 ? null : tempChecks.All( x => Get( x, "status" ) == "COMPLETED" || Get( x, "state" ) is "SUCCESS" or "FAILURE" or "ERROR" );

			return (Get( tempResult, "headRefOid" ), tempIsComplete);
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
			string tempTargetSha = await ReadTargetShaAsync( tempRepository, tempTarget, tCancel );
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

			return JsonSerializer.SerializeToElement( new { PullRequest = tempParts[ 0 ], TargetSha = tempTargetSha, Discussion = tempParts[ 1 ], ReviewComments = tempParts[ 2 ], Reviews = tempParts[ 3 ], LinkedIssues = tempIssues, RelatedPullRequests = tempRelated, RelatedPullRequestLimit = 50, CapturedAt = DateTimeOffset.UtcNow } );
		}

		private async Task<string> ReadTargetShaAsync( string tRepository, string tBranch, CancellationToken tCancel )
		{
			// PR base SHAs may lag behind the current branch tip
			string tempBranch = string.Join( "/", tBranch.Split( '/' ).Select( Uri.EscapeDataString ) );
			JsonElement tempReference = await ReadJsonAsync( [ "api", $"repos/{tRepository}/git/ref/heads/{tempBranch}" ], tCancel );
			JsonElement tempObject = tempReference.GetProperty( "object" );
			string tempSha = Get( tempObject, "sha" );

			if ( Get( tempReference, "ref" ) != $"refs/heads/{tBranch}" || Get( tempObject, "type" ) != "commit" || !Regex.IsMatch( tempSha, "^[a-f0-9]{40}$" ) )
			{
				throw new InvalidOperationException( $"GitHub returned an invalid target branch reference for {tRepository}:{tBranch}" );
			}

			return tempSha;
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
			// Every reported check counts, including checks not required by branch protection
			CommandResult tempResult = await tCommand.RunAsync( "gh", [ "pr", "checks", tUrl, "--json", "name,bucket,state" ], tTimeoutSeconds: 45, tCancel: tCancel );
			string[] tempBuckets = ReadCheckBuckets( tempResult );
			GitHubPullRequest tempCurrent = await ReadAsync( tUrl, tCancel );

			if ( tempCurrent.State != "OPEN" || tempCurrent.HeadSha != tExpected.HeadSha || tempCurrent.TargetBranch != tExpected.TargetBranch || tempCurrent.TargetSha != tExpected.TargetSha || tempCurrent.HeadBranch != tExpected.HeadBranch || !string.Equals( tempCurrent.HeadRepository, tExpected.HeadRepository, StringComparison.OrdinalIgnoreCase ) )
			{
				throw new InvalidOperationException( "PR changed while reading CI; check again before advancing" );
			}

			if ( tempBuckets.Length == 0 )
			{
				return tIsCheckRequired ? CIResult.Pending : CIResult.Passed;
			}

			if ( tempBuckets.Any( x => x == "fail" ) )
			{
				return CIResult.Failed;
			}

			return tempBuckets.All( x => x is "pass" or "skipping" or "cancel" ) ? CIResult.Passed : CIResult.Pending;
		}

		private static string[] ReadCheckBuckets( CommandResult tResult )
		{
			if ( string.IsNullOrWhiteSpace( tResult.Output ) )
			{
				string tempError = tResult.Error.Trim();

				if ( tResult.IsSuccess || ( tResult.ExitCode == 1 && tempError.StartsWith( "no checks reported on the '", StringComparison.Ordinal ) && tempError.EndsWith( "' branch", StringComparison.Ordinal ) ) )
				{
					return [];
				}

				throw new InvalidOperationException( $"Unable to obtain CI checks: {tResult.Failure}" );
			}

			// gh uses exit 1 for failed checks and exit 8 for pending checks
			if ( tResult.ExitCode is not ( 0 or 1 or 8 ) )
			{
				throw new InvalidOperationException( $"Unable to obtain CI checks: {tResult.Failure}" );
			}

			using JsonDocument tempDocument = JsonDocument.Parse( tResult.Output );

			if ( tempDocument.RootElement.ValueKind != JsonValueKind.Array || ( tempDocument.RootElement.GetArrayLength() == 0 && !tResult.IsSuccess ) )
			{
				throw new InvalidOperationException( "Invalid GitHub checks response" );
			}

			return [ .. tempDocument.RootElement.EnumerateArray().Select( x => Get( x, "bucket" ).ToLowerInvariant() ) ];
		}

		public async Task<long> PublishCommentAsync( string tRepository, int tNumber, string tBody, string tMarker, CancellationToken tCancel )
		{
			string tempPath = $"repos/{tRepository}/issues/{tNumber}/comments";
			JsonElement tempPages = await ReadJsonAsync( [ "api", "--paginate", "--slurp", $"{tempPath}?per_page=100" ], tCancel );

			// Recover a successful submission whose response or database save was lost
			foreach ( JsonElement tempPage in tempPages.EnumerateArray() )
			{
				foreach ( JsonElement tempComment in tempPage.EnumerateArray() )
				{
					string tempBody = Get( tempComment, "body" );

					if ( tempBody.Contains( tMarker, StringComparison.Ordinal ) )
					{
						if ( tempBody != tBody )
						{
							throw new InvalidOperationException( "Existing CPR report has different text; inspect the GitHub comment before retrying" );
						}

						return tempComment.GetProperty( "id" ).GetInt64();
					}
				}
			}

			CommandResult tempResult = await tCommand.RunAsync( "gh", [ "api", "--method", "POST", tempPath, "--input", "-" ], tInput: JsonSerializer.Serialize( new { body = tBody } ), tTimeoutSeconds: 60, tCancel: tCancel );

			if ( !tempResult.IsSuccess )
			{
				throw new InvalidOperationException( $"GitHub PR comment failed: {tempResult.Failure}" );
			}

			using JsonDocument tempDocument = JsonDocument.Parse( tempResult.Output );

			return tempDocument.RootElement.GetProperty( "id" ).GetInt64();
		}

		private static string Get( JsonElement tElement, string tProperty )
		{
			return tElement.TryGetProperty( tProperty, out JsonElement tempValue ) && tempValue.ValueKind == JsonValueKind.String ? tempValue.GetString() ?? "" : "";
		}
	}
}
