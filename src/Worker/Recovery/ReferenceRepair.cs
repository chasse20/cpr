namespace CPR.Worker.Recovery
{
	public sealed record ReferenceRepair
	{
		public required string Path { get; set; }
		public required string Area { get; set; }
		public required string FeedbackKey { get; set; }
		public required string FailedCheck { get; set; }
		public required string PassedCheck { get; set; }
		public required string BeforeExcerpt { get; set; }
		public required string AfterExcerpt { get; set; }
		public required string Reason { get; set; }
	}
}
