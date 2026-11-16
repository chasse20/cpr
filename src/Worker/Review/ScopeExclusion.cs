namespace CPR.Worker.Review
{
	public sealed record ScopeExclusion
	{
		public required string[] Paths { get; set; }
		public required string Reason { get; set; }
	}
}
