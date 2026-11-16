using CPR.Worker.Reports;

namespace CPR.Worker.Review
{
	public sealed record ScopeFile
	{
		public required string Path { get; set; }
		public required string Scope { get; set; }
		public required Dictionary<string, Assessment> Rules { get; set; }
	}
}
