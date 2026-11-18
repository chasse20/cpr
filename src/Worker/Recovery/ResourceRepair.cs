namespace CPR.Worker.Recovery
{
	public sealed record ResourceRepair
	{
		public required string BeforeCheck { get; set; }
		public required string AfterCheck { get; set; }
		public required string BeforeExcerpt { get; set; }
		public required string AfterExcerpt { get; set; }
		public required string Reason { get; set; }
	}
}
