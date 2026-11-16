namespace CPR.Worker.Review
{
	public sealed record ReviewScope
	{
		public required string ComparisonBaseSha { get; set; }
		public required string HeadSha { get; set; }
		public required string TargetSha { get; set; }
		public required string[] OriginalPrPaths { get; set; }
		public required string[] WorkingTreePaths { get; set; }
		public required string ScopeKind { get; set; }
		public required string[] ConflictPaths { get; set; }
		public required string[] RequiredPaths { get; set; }
	}
}
