namespace CPR.Worker.Sessions
{
	public sealed record ManualSummary
	{
		public required string Summary { get; set; }
		public required string PrComment { get; set; }
		public required string CommitMessage { get; set; }
	}
}
