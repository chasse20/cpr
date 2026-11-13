namespace CPR.Service.Discord
{
	public sealed class DiscordRequestException( string tMessage, TimeSpan tRetryAfter, bool tIsPermanent ) : Exception( tMessage )
	{
		public TimeSpan RetryAfter { get; } = tRetryAfter;
		public bool IsPermanent { get; } = tIsPermanent;
	}
}
