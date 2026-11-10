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
			CommandResult tempResult = await tCommand.RunAsync( "gh", [ "pr", "view", tUrl, "--json", "state,number,baseRefName,headRefName,headRefOid,headRepository,isCrossRepository,mergeable,mergeStateStatus,isDraft" ], tTimeoutSeconds: 45, tCancel: tCancel );

			if ( !tempResult.IsSuccess )
			{
				throw new InvalidOperationException( $"GitHub PR lookup failed: {tempResult.Failure}" );
			}

			using JsonDocument tempDocument = JsonDocument.Parse( tempResult.Output );
			JsonElement tempRoot = tempDocument.RootElement;
			string tempHeadRepository = tempRoot.TryGetProperty( "headRepository", out JsonElement tempHead ) && tempHead.ValueKind == JsonValueKind.Object && tempHead.TryGetProperty( "nameWithOwner", out JsonElement tempFull ) ? tempFull.GetString() ?? tempRepository : tempRepository;

			return new( tempRepository, tempNumber, Get( tempRoot, "baseRefName" ), tempHeadRepository.ToLowerInvariant(), Get( tempRoot, "headRefName" ), Get( tempRoot, "headRefOid" ), Get( tempRoot, "state" ), tempRoot.TryGetProperty( "isDraft", out JsonElement tempDraft ) && tempDraft.ValueKind == JsonValueKind.True, Get( tempRoot, "mergeable" ), Get( tempRoot, "mergeStateStatus" ), tempRoot.TryGetProperty( "isCrossRepository", out JsonElement tempCross ) && tempCross.ValueKind == JsonValueKind.True );
		}

		public async Task<CIResult> ReadCIAsync( string tUrl, bool tIsCheckRequired, CancellationToken tCancel )
		{
			CommandResult tempResult = await tCommand.RunAsync( "gh", [ "pr", "checks", tUrl, "--required", "--json", "name,bucket,state" ], tTimeoutSeconds: 45, tCancel: tCancel );

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
