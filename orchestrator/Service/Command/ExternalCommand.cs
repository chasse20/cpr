namespace CPR.Service.Command
{
	public sealed class ExternalCommand( IConfiguration tConfiguration )
	{
		public async Task<CommandResult> RunAsync( string tExecutable, IEnumerable<string> tArguments, string? tInput = null, int tTimeoutSeconds = 120, CancellationToken tCancel = default )
		{
			using CancellationTokenSource tempTimeout = CancellationTokenSource.CreateLinkedTokenSource( tCancel );
			tempTimeout.CancelAfter( TimeSpan.FromSeconds( tTimeoutSeconds ) );

			ProcessStartInfo tempInfo = new( tExecutable )
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8
			};

			foreach ( string tempArgument in tArguments )
			{
				tempInfo.ArgumentList.Add( tempArgument );
			}

			string? tempToken = tConfiguration[ "GH_TOKEN" ];

			if ( !string.IsNullOrWhiteSpace( tempToken ) )
			{
				tempInfo.Environment[ "GH_TOKEN" ] = tempToken;
			}

			using Process tempProcess = new()
			{
				StartInfo = tempInfo
			};

			try
			{
				tempProcess.Start();
				Task<string> tempOutputTask = tempProcess.StandardOutput.ReadToEndAsync( tempTimeout.Token );
				Task<string> tempErrorTask = tempProcess.StandardError.ReadToEndAsync( tempTimeout.Token );

				if ( tInput is not null )
				{
					await tempProcess.StandardInput.WriteAsync( tInput.AsMemory(), tempTimeout.Token );
				}

				tempProcess.StandardInput.Close();
				await tempProcess.WaitForExitAsync( tempTimeout.Token );

				return new( tempProcess.ExitCode, await tempOutputTask, await tempErrorTask );
			}
			catch ( OperationCanceledException )
			{
				try
				{
					if ( !tempProcess.HasExited )
					{
						tempProcess.Kill( entireProcessTree: true );
					}
				}
				catch ( InvalidOperationException )
				{
					// The process already exited
				}

				tCancel.ThrowIfCancellationRequested();
				throw new TimeoutException( $"Command '{tExecutable}' exceeded {tTimeoutSeconds}s timeout" );
			}
		}
	}
}
