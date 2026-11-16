namespace CPR.Protocol
{
	public sealed record WorkerSnapshot( string TreeSha, string? SessionId, string TargetSha, string RunKey, bool IsHistorical );
}
