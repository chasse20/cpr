using CPR.Service.GitHub;
using CPR.SQL;

namespace CPR.Service.Queue
{
	public static class WorkflowRules
	{
		public static WorkflowDecision Decide( PullRequest tRequest, bool tIsFront, GitHubPullRequest tRemote, CIResult tChecks )
		{
			if ( tRequest.Status is not ( PullRequestStatus.WaitingForCi or PullRequestStatus.WaitingForMergeTurn or PullRequestStatus.ReadyToBeMerged ) )
			{
				return new( tRequest.Status, tRequest.WorkReason );
			}

			// CI failures require an explicit interactive recovery request
			if ( tChecks == CIResult.Failed )
			{
				return new( PullRequestStatus.CiFailure, tRequest.WorkReason );
			}

			if ( tIsFront && ( tRemote.Mergeable.Equals( "CONFLICTING", StringComparison.OrdinalIgnoreCase ) || tRemote.MergeStateStatus.Equals( "DIRTY", StringComparison.OrdinalIgnoreCase ) || tRemote.MergeStateStatus.Equals( "BEHIND", StringComparison.OrdinalIgnoreCase ) ) )
			{
				return new( PullRequestStatus.WaitingToBeProcessed, PullRequestWorkReason.MergeConflict );
			}

			if ( tChecks == CIResult.Pending )
			{
				return new( PullRequestStatus.WaitingForCi, tRequest.WorkReason );
			}

			if ( !tIsFront )
			{
				return new( PullRequestStatus.WaitingForMergeTurn, tRequest.WorkReason );
			}

			bool tempIsReady = !tRemote.IsDraft && tRemote.Mergeable.Equals( "MERGEABLE", StringComparison.OrdinalIgnoreCase ) && tRemote.MergeStateStatus.Equals( "CLEAN", StringComparison.OrdinalIgnoreCase );

			return new( tempIsReady ? PullRequestStatus.ReadyToBeMerged : PullRequestStatus.WaitingForMergeTurn, tRequest.WorkReason );
		}
	}
}
