using CPR.SQL;

namespace CPR.Service.Queue
{
	public static class PromptRenderer
	{
		public static string Render( string tTemplate, PullRequest tRequest )
		{
			return tTemplate
				.Replace( "{{PullRequestUrl}}", $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", StringComparison.Ordinal )
				.Replace( "{{Repository}}", tRequest.Repository, StringComparison.Ordinal )
				.Replace( "{{TargetBranch}}", tRequest.TargetBranch, StringComparison.Ordinal )
				.Replace( "{{Number}}", tRequest.Number.ToString( CultureInfo.InvariantCulture ), StringComparison.Ordinal )
				.Replace( "{{CprId}}", tRequest.PullRequestId.ToString( CultureInfo.InvariantCulture ), StringComparison.Ordinal );
		}
	}
}
