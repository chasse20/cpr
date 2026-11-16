namespace CPR.SQL
{
	// Values are persisted in PostgreSQL; never renumber or reuse them
	public enum PullRequestWorkReason
	{
		InitialReview = 0,
		MergeConflict = 1,
		CiRecovery = 2, // Retained for persisted work; new manual sessions use ManualTweak
		Interactive = 3, // Retained for persisted work; new manual sessions use ManualTweak
		Rereview = 4,
		ManualTweak = 5,
		ReportRetry = 6,
		FeedbackRepair = 7
	}
}
