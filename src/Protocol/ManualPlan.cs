namespace CPR.Protocol
{
	public sealed record ManualPlan( string HeadSha, string TargetSha, string? MaintainerResolution, string? PublishedSha, string SummaryPrompt, string SummaryModel, string SummaryReasoningEffort, int SummaryTimeoutSeconds );
}
