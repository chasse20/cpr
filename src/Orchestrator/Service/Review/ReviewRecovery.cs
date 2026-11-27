using CPR.SQL;

namespace CPR.Service.Review
{
	public sealed record ReviewRecovery
	{
		public string? OperationId { get; set; }
		public int Attempts { get; set; }
		public bool IsPending { get; set; }
		public bool IsContinuation { get; set; }
		public bool IsFullReviewRequested { get; set; }
		public PullRequestWorkReason? ResumeWorkReason { get; set; }
		public DateTimeOffset? NextAttemptAt { get; set; }
		public string? Category { get; set; }
		public string? Action { get; set; }

		public static ReviewRecovery Read( PullRequest tRequest )
		{
			return JsonSerializer.Deserialize<ReviewRecovery>( tRequest.Recovery )!;
		}

		public void Save( PullRequest tRequest )
		{
			tRequest.Recovery = JsonSerializer.Serialize( this );
		}
	}
}
