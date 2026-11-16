using CPR.Worker.Review;

namespace CPR.Worker.Reports
{
	public sealed record ScopeAudit
	{
		public required string ComparisonBaseSha { get; set; }
		public required string HeadSha { get; set; }
		public required string TargetSha { get; set; }
		public required ScopeFile[] Files { get; set; }
		public required ScopeExclusion[] Exclusions { get; set; }
	}
}
