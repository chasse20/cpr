namespace CPR.Protocol
{
	public sealed record WorkerReviewState( string Status, string? Category, string? Action, WorkerSnapshot? Snapshot );
}
