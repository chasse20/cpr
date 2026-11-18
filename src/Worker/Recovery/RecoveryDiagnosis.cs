namespace CPR.Worker.Recovery
{
	public sealed record RecoveryDiagnosis
	{
		public required string Category { get; set; }
		public required string Summary { get; set; }
		public required string NextAction { get; set; }
		public required ReferenceRepair[] ReferenceRepairs { get; set; }
		public required ResourceRepair? ResourceRepair { get; set; }
	}
}
