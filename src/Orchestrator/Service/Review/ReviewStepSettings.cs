namespace CPR.Service.Review
{
	public sealed record ReviewStepSettings
	{
		public bool IsEnabled { get; set; } = true;
		public string Name { get; set; } = "review";
		public string Model { get; set; } = "gpt-6.2-astra";
		public string ReasoningEffort { get; set; } = "xhigh";
		public int MaxAttempts { get; set; } = 2;
		public PromptKind[] Prompts { get; set; } = [];
	}
}
