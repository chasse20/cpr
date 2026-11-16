namespace CPR.Worker.Reports
{
	public sealed record Relevance
	{
		public required string Status { get; set; }
		public required string Details { get; set; }
		public required string[] RelatedPrs { get; set; }
		public required string Evidence { get; set; }
	}
}
