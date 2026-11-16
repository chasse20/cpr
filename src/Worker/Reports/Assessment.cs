namespace CPR.Worker.Reports
{
	public sealed record Assessment
	{
		public required string Status { get; set; }
		public required string Details { get; set; }
		public required List<string> Checks { get; set; }
	}
}
