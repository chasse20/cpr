namespace CPR.Worker.Reports
{
	public sealed record ReviewResult
	{
		public string FailureKind { get; set; } = "none";
		public required string Outcome { get; set; }
		public required string Summary { get; set; }
		public required string CommitMessage { get; set; }
		public required string PrComment { get; set; }
		public required string ScopeAudit { get; set; }
		public required Relevance Relevance { get; set; }
		public required Dictionary<string, Assessment> Coverage { get; set; }
		public required bool IsResourceMeasurementRequired { get; set; }
		public required ReviewCheck[] Checks { get; set; }
		public required ReviewFinding[] Findings { get; set; }
		public required List<string> Limitations { get; set; }
		public required string FollowUp { get; set; }
	}
}
