namespace CPR.Service.Review
{
	public sealed record ReviewPlan( string HeadSha, bool IsValidationOnly, bool IsRereview, JsonElement Context, ReviewStep[] Steps, string OperationId, string? MaintainerResolution, string? PublishedSha, bool IsReportRetry, ReviewStep? ReportRepairStep, bool IsMergeRepair, ReviewReport? PriorReview, bool IsHistoricalReport, ReviewStep SavedValidationStep, ReviewStep? RecoveryStep, bool IsFeedbackRepair, CPR.Service.GitHub.FeedbackComment[] Feedback );
}
