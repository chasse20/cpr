namespace CPR.Service.SSH
{
	public sealed record WorkerSnapshot( string TreeSha, string? SessionId, string TargetSha );
}
