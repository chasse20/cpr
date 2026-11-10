namespace CPR.SQL
{
	public enum PullRequestStatus
	{
		WaitingToBeProcessed,
		Processing,
		ReadyForApproval,
		WaitingForCi,
		CiFailure,
		WaitingForMergeTurn,
		ReadyToBeMerged
	}
}
