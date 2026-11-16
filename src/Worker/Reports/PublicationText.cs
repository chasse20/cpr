namespace CPR.Worker.Reports
{
	public sealed record PublicationText
	{
		public required string CommitMessage { get; set; }
		public required string PrComment { get; set; }
	}
}
