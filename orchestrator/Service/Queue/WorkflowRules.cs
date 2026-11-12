using CPR.Service.GitHub;
using CPR.SQL;

namespace CPR.Service.Queue
{
	public static class WorkflowRules
	{
		public static bool IsSameSource( PullRequest tRequest, GitHubPullRequest tRemote )
		{
			return tRemote.State == "OPEN" && tRequest.RemoteSha == tRemote.HeadSha && tRequest.TargetBranch == tRemote.TargetBranch && tRequest.HeadBranch == tRemote.HeadBranch && string.Equals( tRequest.HeadRepository, tRemote.HeadRepository, StringComparison.OrdinalIgnoreCase );
		}

		public static void Block( PullRequest tRequest, string tReason )
		{
			tRequest.Status = PullRequestStatus.Blocked;
			tRequest.PreparedSha = null;
			tRequest.ApprovedSha = null;
			tRequest.ReviewedTargetSha = null;
			tRequest.LastError = tReason[ ..Math.Min( 4000, tReason.Length ) ];
		}

		public static WorkflowDecision Decide( PullRequest tRequest, bool tIsFront, GitHubPullRequest tRemote, CIResult tChecks )
		{
			if ( tRequest.Status is not ( PullRequestStatus.WaitingForCi or PullRequestStatus.CiFailure or PullRequestStatus.WaitingForMergeTurn or PullRequestStatus.ReadyToBeMerged ) )
			{
				return new( tRequest.Status, tRequest.WorkReason );
			}

			// Poll reruns on the published commit; code repair remains an explicit request
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
