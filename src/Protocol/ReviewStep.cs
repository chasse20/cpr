namespace CPR.Protocol
{
	public sealed record ReviewStep( string Name, string Model, string ReasoningEffort, int TimeoutSeconds, int MaxAttempts, string Prompt );
}
