namespace CPR.Protocol
{
	public sealed record ReviewPlan( string HeadSha, bool IsRereview, JsonElement Context, ReviewStep[] Steps, string OperationId, string? MaintainerResolution, string? PublishedSha, bool IsReportRetry, bool IsMergeRepair, ReviewReport? PriorReview, bool IsHistoricalReport, ReviewStep SavedValidationStep, ReviewStep? RecoveryStep, bool IsFeedbackRepair, FeedbackComment[] Feedback, string? ApprovedTreeSha, bool IsContinuation, int RecoveryAttempt, ReviewStep? HeadReassessmentStep = null );
}
