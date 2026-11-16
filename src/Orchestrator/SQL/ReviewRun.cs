namespace CPR.SQL
{
	[Table( "ReviewRun", Schema = "cpr" )]
	[Index( nameof( PullRequestId ), nameof( WorkerId ), nameof( RunKey ), IsUnique = true )]
	public sealed class ReviewRun
	{
		[Key]
		[DatabaseGenerated( DatabaseGeneratedOption.Identity )]
		public long ReviewRunId { get; set; }
		public long PullRequestId { get; set; }
		public int WorkerId { get; set; }
		[Required, MaxLength( 128 )]
		public required string RunKey { get; set; }
		[Required, Column( TypeName = "jsonb" )]
		public required string Details { get; set; }
		public string? CommentBody { get; set; }
		[MaxLength( 64 )]
		public string? PublishedSha { get; set; }
		public long? CommentId { get; set; }
		[MaxLength( 4000 )]
		public string? CommentError { get; set; }
		public DateTimeOffset? CommentAttemptedAt { get; set; }
		public DateTimeOffset? CommentPublishedAt { get; set; }
		[ForeignKey( nameof( PullRequestId ) )]
		[DeleteBehavior( DeleteBehavior.Restrict )]
		public PullRequest? PullRequest { get; set; }
		[ForeignKey( nameof( WorkerId ) )]
		[DeleteBehavior( DeleteBehavior.Restrict )]
		public Worker? Worker { get; set; }
	}
}
