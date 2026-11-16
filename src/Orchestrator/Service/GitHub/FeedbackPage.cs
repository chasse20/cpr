namespace CPR.Service.GitHub
{
	public sealed record FeedbackPage( string ETag, bool IsNextPage, JsonElement[] Comments );
}
