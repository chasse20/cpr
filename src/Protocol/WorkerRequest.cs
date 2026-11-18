namespace CPR.Protocol
{
	public sealed record WorkerRequest( int Version, long Id, string Repository, int Number, string TargetBranch, string GitName, string GitEmail, bool IsPreserved = false, ReviewPlan? Plan = null, ManualPlan? Manual = null, string? ApprovedTree = null, string? OperationId = null, Dictionary<string, string>? KnownHistory = null, string? ApprovedCommit = null, string? ReviewedTarget = null );
}
