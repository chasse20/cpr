namespace CPR.SQL
{
	[Table( "PullRequest", Schema = "dbo" )]
	[Index( nameof( Repository ), nameof( Number ), IsUnique = true )]
	[Index( nameof( Repository ), nameof( TargetBranch ), nameof( PullRequestId ) )]
	[Index( nameof( WorkerId ), IsUnique = true )]
	public sealed class PullRequest
	{
		[Key]
		[DatabaseGenerated( DatabaseGeneratedOption.Identity )]
		public long PullRequestId { get; set; }
		[Required, MaxLength( 255 )]
		public required string Repository { get; set; }
		[Range( 1, int.MaxValue )]
		public int Number { get; set; }
		[Required, MaxLength( 255 )]
		public required string TargetBranch { get; set; }
		[Required, MaxLength( 255 )]
		public required string HeadRepository { get; set; }
		[Required, MaxLength( 255 )]
		public required string HeadBranch { get; set; }
		[Required, MaxLength( 64 )]
		public required string RemoteSha { get; set; }
		public PullRequestStatus Status { get; set; } = PullRequestStatus.WaitingToBeProcessed;
		public PullRequestWorkReason WorkReason { get; set; } = PullRequestWorkReason.InitialReview;
		public int? WorkerId { get; set; }
		[MaxLength( 128 )]
		public string? SessionId { get; set; }
		// Prepared and approved snapshots hold Git tree hashes
		[MaxLength( 64 )]
		public string? PreparedSha { get; set; }
		[MaxLength( 64 )]
		public string? ApprovedSha { get; set; }
		[MaxLength( 64 )]
		public string? PublishedSha { get; set; }
		[MaxLength( 4096 )]
		public string? LastError { get; set; }
		public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
		[ForeignKey( nameof( WorkerId ) )]
		[DeleteBehavior( DeleteBehavior.Restrict )]
		public Worker? Worker { get; set; }
	}
}
