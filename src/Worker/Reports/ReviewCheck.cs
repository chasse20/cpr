namespace CPR.Worker.Reports
{
	public sealed record ReviewCheck
	{
		public required string Id { get; set; }
		public string Kind { get; set; } = "command";
		public required string Command { get; set; }
		public required string Status { get; set; }
		public required int? ExitCode { get; set; }
		public required bool IsRequired { get; set; }
		public required bool IsBaselineFailure { get; set; }
		public required string Details { get; set; }
		public required string Artifact { get; set; }
		public string? SupersededBy { get; set; }
	}
}
