namespace CPR.Protocol
{
	public sealed record ReviewReport( long ReviewRunId, string RunKey, string HeadSha, string TargetSha, string TreeSha, string Body );
}
