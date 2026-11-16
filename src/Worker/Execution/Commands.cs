namespace CPR.Worker.Execution
{
	public static class Commands
	{
		public static async Task<CommandResult> RunAsync( string tExecutable, IEnumerable<string> tArguments, string? tDirectory = null, string? tInput = null, Dictionary<string, string>? tEnvironment = null, CancellationToken tCancel = default )
		{
			ProcessStartInfo tempInfo = CreateStartInfo( tExecutable, tArguments, tDirectory );
			if ( tEnvironment is not null )
			{
				foreach ( KeyValuePair<string, string> tempPair in tEnvironment )
				{
					tempInfo.Environment[ tempPair.Key ] = tempPair.Value;
				}
			}

			using Process tempProcess = new() { StartInfo = tempInfo };
			tempProcess.Start();
			Task<string> tempOutput = tempProcess.StandardOutput.ReadToEndAsync();
			Task<string> tempError = tempProcess.StandardError.ReadToEndAsync();
			try
			{
				if ( tInput is not null )
				{
					await tempProcess.StandardInput.WriteAsync( tInput.AsMemory(), tCancel );
				}
				tempProcess.StandardInput.Close();
				await tempProcess.WaitForExitAsync( tCancel );
			}
			catch
			{
				if ( !tempProcess.HasExited )
				{
					tempProcess.Kill( true );
				}
				await tempProcess.WaitForExitAsync( CancellationToken.None );
				await Task.WhenAll( tempOutput, tempError );
				throw;
			}

			return new( tempProcess.ExitCode, await tempOutput, await tempError );
		}

		public static ProcessStartInfo CreateStartInfo( string tExecutable, IEnumerable<string> tArguments, string? tDirectory = null )
		{
			ProcessStartInfo tempInfo = new( tExecutable ) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = tDirectory ?? Environment.CurrentDirectory };
			tempInfo.Environment[ "GIT_TERMINAL_PROMPT" ] = "0";
			foreach ( string tempArgument in tArguments )
			{
				tempInfo.ArgumentList.Add( tempArgument );
			}

			return tempInfo;
		}
	}
}
