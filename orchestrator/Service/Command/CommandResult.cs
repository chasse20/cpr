namespace CPR.Service.Command
{
	public sealed record CommandResult( int ExitCode, string Output, string Error )
	{
		public bool IsSuccess => ExitCode == 0;
		public string Failure => string.IsNullOrWhiteSpace( Error ) ? Output.Trim() : Error.Trim();
	}
}
