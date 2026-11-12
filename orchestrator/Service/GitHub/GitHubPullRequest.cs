namespace CPR.Service.GitHub
{
	public sealed record GitHubPullRequest( string Repository, int Number, string TargetBranch, string HeadRepository, string HeadBranch, string HeadSha, string State, bool IsDraft, string Mergeable, string MergeStateStatus, bool IsCrossRepository, DateTimeOffset? CompletedAt = null, string TargetSha = "" )
	{
		public string Url => $"https://github.com/{Repository}/pull/{Number}";
	}
}
