namespace CPR.Service.GitHub
{
	public sealed record FeedbackComment( string Key, string Version, string Url, string Body, string Path, string CommitSha );
}
