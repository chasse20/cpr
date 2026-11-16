namespace CPR.Worker.Recovery
{
	public sealed record SavedValidationResult
	{
		public required bool IsValidated { get; set; }
		public required string[] Issues { get; set; }
		public required string Summary { get; set; }
	}
}
