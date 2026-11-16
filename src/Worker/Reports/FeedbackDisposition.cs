namespace CPR.Worker.Reports
{
	public sealed record FeedbackDisposition
	{
		public required string Key { get; set; }
		public required string Version { get; set; }
		public required string Status { get; set; }
		public required string Reason { get; set; }
		public required string[] Checks { get; set; }
	}
}
