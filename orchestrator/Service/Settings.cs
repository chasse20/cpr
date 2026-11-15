using CPR.Service.Discord;
using CPR.Service.Review;

namespace CPR.Service
{
	public sealed record Settings
	{
		public DiscordSettings Discord { get; set; } = new();
		public int PollSeconds { get; set; } = 60;
		public bool IsWorkerExecutionEnabled { get; set; }
		public bool IsPublicationEnabled { get; set; }
		public bool IsCICheckRequired { get; set; } = true;
		public string GitAuthorName { get; set; } = "";
		public string GitAuthorEmail { get; set; } = "";
		public string GitStoragePath { get; set; } = "/data/git";
		public Dictionary<PromptKind, string> Prompts { get; set; } = [];
		public ReviewStepSettings ReportRepairStep { get; set; } = new() { Name = "report-repair", Model = "gpt-6-sol", ReasoningEffort = "high", TimeoutSeconds = 180, MaxAttempts = 1, Prompts = [ PromptKind.ReportRepair ] };
		public ReviewStepSettings MergeRepairStep { get; set; } = new() { Name = "merge-repair", Model = "gpt-6-sol", ReasoningEffort = "high", TimeoutSeconds = 900, MaxAttempts = 1, Prompts = [ PromptKind.MergeRepair, PromptKind.Correctness ] };
		public ReviewStepSettings[] ReviewSteps { get; set; } = [];
	}
}
