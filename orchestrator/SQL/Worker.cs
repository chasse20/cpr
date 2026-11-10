namespace CPR.SQL
{
	[Table( "Worker", Schema = "dbo" )]
	[Index( nameof( SshHost ), IsUnique = true )]
	public sealed class Worker
	{
		[Key]
		[DatabaseGenerated( DatabaseGeneratedOption.None )]
		public int WorkerId { get; set; }
		[Required, MaxLength( 100 )]
		public required string SshHost { get; set; }
		public WorkerStatus Status { get; set; } = WorkerStatus.Offline;
		public DateTimeOffset? LastSeenAt { get; set; }
		public PullRequest? PullRequest { get; set; }
	}
}
