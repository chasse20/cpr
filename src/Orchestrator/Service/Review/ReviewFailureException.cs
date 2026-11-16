namespace CPR.Service.Review
{
	public sealed class ReviewFailureException( string tCategory, string tMessage ) : InvalidOperationException( tMessage )
	{
		public string Category { get; } = tCategory;

		public static bool IsTransient( string tMessage )
		{
			return !Regex.IsMatch( tMessage, "authentication|permission denied|bad credentials|HTTP 40[134]|host key verification", RegexOptions.IgnoreCase ) &&
				Regex.IsMatch( tMessage, "connection (reset|closed|refused|timed out)|broken pipe|temporary failure|could not resolve|network is unreachable|TLS handshake timeout|HTTP (429|5[0-9]{2})|service unavailable|rate limit|unexpected EOF", RegexOptions.IgnoreCase );
		}
	}
}
