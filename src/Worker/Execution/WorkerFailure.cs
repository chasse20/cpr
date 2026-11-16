namespace CPR.Worker.Execution
{
	public sealed class WorkerFailure( string tCategory, string tMessage, int tExitCode = 13 ) : Exception( tMessage )
	{
		public string Category { get; } = tCategory == "infrastructure" && IsTransient( tMessage ) ? "transient" : tCategory;
		public int ExitCode { get; } = tExitCode;

		public static bool IsTransient( string tMessage )
		{
			return !Regex.IsMatch( tMessage, "authentication|permission denied|bad credentials|HTTP 40[134]|host key verification", RegexOptions.IgnoreCase ) &&
				Regex.IsMatch( tMessage, "connection (reset|closed|refused|timed out)|broken pipe|temporary failure|could not resolve|network is unreachable|TLS handshake timeout|HTTP (429|5[0-9]{2})|service unavailable|rate limit|unexpected EOF", RegexOptions.IgnoreCase );
		}

		public static void Require( bool tCondition, string tMessage, string tCategory = "snapshot" )
		{
			if ( !tCondition )
			{
				throw new WorkerFailure( tCategory, tMessage );
			}
		}
	}
}
