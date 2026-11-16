namespace CPR.Worker.Reports
{
	public sealed record ReviewFinding
	{
		public required string Severity { get; set; }
		public required string Location { get; set; }
		public required string Trigger { get; set; }
		public required string Expected { get; set; }
		public required string Actual { get; set; }
		public required string Impact { get; set; }
		public required string Origin { get; set; }
		public required string Status { get; set; }
		public required string Evidence { get; set; }
		public required string Repair { get; set; }
	}
}
