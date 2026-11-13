namespace CPR.SQL
{
	[Table( "DiscordNotification", Schema = "cpr" )]
	[Index( nameof( WebhookId ), nameof( DiscordNotificationId ) )]
	public sealed class DiscordNotification
	{
		[Key]
		[DatabaseGenerated( DatabaseGeneratedOption.Identity )]
		public long DiscordNotificationId { get; set; }
		public long PullRequestId { get; set; }
		[Required, MaxLength( 20 )]
		public required string WebhookId { get; set; }
		public PullRequestStatus Status { get; set; }
		[MaxLength( 1000 )]
		public string? Detail { get; set; }
		public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
		[ForeignKey( nameof( PullRequestId ) )]
		[DeleteBehavior( DeleteBehavior.Restrict )]
		public required PullRequest PullRequest { get; set; }
	}
}
