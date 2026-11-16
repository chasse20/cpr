namespace CPR.SQL
{
	// Values are persisted in PostgreSQL; never renumber or reuse them
	public enum PullRequestStatus
	{
		WaitingToBeProcessed = 0,
		Processing = 1,
		ReadyForApproval = 2,
		WaitingForCi = 3,
		CiFailure = 4,
		WaitingForMergeTurn = 5,
		ReadyToBeMerged = 6,
		Merged = 7,
		Closed = 8,
		Blocked = 9,
		Removed = 10
	}
}
