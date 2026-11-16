namespace CPR.Worker.Execution
{
	public sealed record CommandResult( int ExitCode, string Output, string Error )
	{
		public bool IsSuccess => ExitCode == 0;
	}
}
