namespace CPR.Service
{
	public sealed record Settings
	{
		public int PollSeconds { get; set; } = 60;
		public bool IsWorkerExecutionEnabled { get; set; }
		public bool IsPublicationEnabled { get; set; }
		public bool IsCICheckRequired { get; set; } = true;
		public string GitAuthorName { get; set; } = "";
		public string GitAuthorEmail { get; set; } = "";
		public string GitStoragePath { get; set; } = "/data/git";
	}
}
