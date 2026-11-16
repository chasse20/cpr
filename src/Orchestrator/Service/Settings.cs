using CPR.Service.Discord;
using CPR.Service.Review;

namespace CPR.Service
{
	public sealed record Settings
	{
		public DiscordSettings Discord { get; set; } = new();
		public int PollSeconds { get; set; } = 60;
		public bool IsCodeRabbitEnabled { get; set; } = true;
		public int CodeRabbitPollSeconds { get; set; } = 10;
		public int CodeRabbitQuietSeconds { get; set; } = 15;
		public int CodeRabbitFallbackQuietSeconds { get; set; } = 120;
		public ReviewStepSettings FeedbackRepairStep { get; set; } = new() { Name = "feedback-repair", Model = "gpt-6-sol", ReasoningEffort = "high", TimeoutSeconds = 0, MaxAttempts = 1, Prompts = [ PromptKind.FeedbackRepair, PromptKind.Correctness, PromptKind.Evidence ] };
		public bool IsWorkerExecutionEnabled { get; set; }
		public bool IsPublicationEnabled { get; set; }
		public bool IsCICheckRequired { get; set; } = true;
		public string GitAuthorName { get; set; } = "";
		public string GitAuthorEmail { get; set; } = "";
		public string GitStoragePath { get; set; } = "/data/git";
		public Dictionary<PromptKind, string> Prompts { get; set; } = [];
		public ReviewStepSettings ReportRepairStep { get; set; } = new() { Name = "report-repair", Model = "gpt-6-sol", ReasoningEffort = "high", TimeoutSeconds = 180, MaxAttempts = 1, Prompts = [ PromptKind.ReportRepair ] };
		public ReviewStepSettings RecoveryStep { get; set; } = new() { Name = "report-recovery", Model = "gpt-6-astra", ReasoningEffort = "low", TimeoutSeconds = 180, MaxAttempts = 1, Prompts = [ PromptKind.ReportRecovery ] };
		public ReviewStepSettings MergeRepairStep { get; set; } = new() { Name = "merge-repair", Model = "gpt-6-sol", ReasoningEffort = "high", TimeoutSeconds = 900, MaxAttempts = 1, Prompts = [ PromptKind.MergeRepair, PromptKind.Correctness ] };
		public ReviewStepSettings[] ReviewSteps { get; set; } = [];
	}
}
