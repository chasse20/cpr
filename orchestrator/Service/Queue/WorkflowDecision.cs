using CPR.SQL;

namespace CPR.Service.Queue
{
	public sealed record WorkflowDecision( PullRequestStatus Status, PullRequestWorkReason WorkReason );
}
