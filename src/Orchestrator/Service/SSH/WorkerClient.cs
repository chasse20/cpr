using CPR.Service.Command;
using CPR.Service.Review;
using CPR.SQL;

namespace CPR.Service.SSH
{
	public sealed class WorkerClient( ExternalCommand tCommand, IOptions<Settings> tSettings )
	{
		private readonly Settings _settings = tSettings.Value;

		public async Task<bool> IsHealthyAsync( Worker tWorker, CancellationToken tCancel )
		{
			try
			{
				CommandResult tempResult = await tCommand.RunAsync( "ssh", [ "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=6", "-T", tWorker.SshHost, "cpr-worker health" ], tTimeoutSeconds: 12, tCancel: tCancel );

				return tempResult.IsSuccess && tempResult.Output.Split( '\n' ).Any( x => x.Trim() == "CPR_WORKER|1" );
			}
			catch ( Exception ) when ( !tCancel.IsCancellationRequested )
			{
				return false;
			}
		}

		public async Task<WorkerSnapshot> ExecuteAsync( Worker tWorker, PullRequest tRequest, ReviewPlan tPlan, CancellationToken tCancel )
		{
			bool tempIsPreserved = tPlan.IsReportRetry || ( tPlan.IsContinuation && tRequest.WorkspaceWorkerId is not null ) || tPlan.ApprovedTreeSha is not null || ( tPlan.IsFeedbackRepair && tRequest.WorkspaceWorkerId is not null && tRequest.PublishedSha is null );
			string tempOutput = await RunAsync( tWorker, "run", Request( tRequest ) with { Plan = tPlan, IsPreserved = tempIsPreserved }, 0, tCancel );

			return ReadSnapshot( tempOutput );
		}

		public async Task<string?> PrepareInteractiveAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, "prepare", Request( tRequest ) with { IsPreserved = tRequest.PublishedSha is null }, 120, tCancel );
			string tempSession = ReadLine( tempOutput, "CPR_SESSION" );

			return Regex.IsMatch( tempSession, "^[A-Za-z0-9_-]{6,128}$" ) ? tempSession : null;
		}

		public async Task<WorkerSnapshot> CompleteTweakAsync( Worker tWorker, PullRequest tRequest, string tTargetSha, CancellationToken tCancel )
		{
			ReviewStepSettings tempStep = _settings.ReviewSteps.Last( x => x.IsEnabled );
			ManualPlan tempPlan = new( tRequest.RemoteSha, tTargetSha, tRequest.MaintainerResolution, tRequest.PublishedSha, _settings.Prompts[ PromptKind.TweakSummary ], tempStep.Model, "low" );
			string tempOutput = await RunAsync( tWorker, "manual-tweak", Request( tRequest ) with { IsPreserved = true, Manual = tempPlan }, 0, tCancel );

			return ReadSnapshot( tempOutput );
		}

		public async Task<string> CommitApprovedAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, "commit", Request( tRequest ) with { IsPreserved = true, ApprovedTree = tRequest.ApprovedSha }, 120, tCancel );
			string tempSha = ReadLine( tempOutput, "CPR_COMMIT" );
			if ( !Regex.IsMatch( tempSha, "^[a-f0-9]{40}$" ) )
			{
				throw new InvalidOperationException( "Invalid worker commit SHA" );
			}

			return tempSha;
		}

		public Task ReleaseAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			return RunAsync( tWorker, "release", Request( tRequest ), 120, tCancel );
		}

		public async Task MoveApprovedAsync( Worker tSource, Worker tDestination, PullRequest tRequest, CancellationToken tCancel )
		{
			WorkerRequest tempRequest = Request( tRequest ) with { IsPreserved = true, ApprovedTree = tRequest.ApprovedSha, ReviewedTarget = tRequest.ReviewedTargetSha };
			string tempCommit = ReadLine( await RunAsync( tSource, "commit-parked", tempRequest, 120, tCancel ), "CPR_COMMIT" );
			if ( !Regex.IsMatch( tempCommit, "^[a-f0-9]{40}$" ) )
			{
				throw new InvalidOperationException( "Invalid parked worker commit SHA" );
			}

			string tempDirectory = Path.GetFullPath( Path.Combine( _settings.GitStoragePath, tRequest.PullRequestId.ToString( CultureInfo.InvariantCulture ) ) );
			Directory.CreateDirectory( tempDirectory );
			await GitAsync( [ "init", "--bare", tempDirectory ], tCancel );
			await GitAsync( [ "-C", tempDirectory, "fetch", "--no-tags", $"ssh://{tSource.SshHost}/workspace/jobs/{tRequest.PullRequestId}/repo", "HEAD" ], tCancel );
			string tempFetched = await GitAsync( [ "-C", tempDirectory, "rev-parse", "FETCH_HEAD" ], tCancel );
			string tempTree = await GitAsync( [ "-C", tempDirectory, "rev-parse", "FETCH_HEAD^{tree}" ], tCancel );
			if ( tempFetched != tempCommit || tempTree != tRequest.ApprovedSha )
			{
				throw new InvalidOperationException( "Saved worker commit changed during transfer; original checkout retained" );
			}

			await GitAsync( [ "-C", tempDirectory, "merge-base", "--is-ancestor", tRequest.RemoteSha, tempCommit ], tCancel );
			await GitAsync( [ "-C", tempDirectory, "merge-base", "--is-ancestor", tRequest.ReviewedTargetSha!, tempCommit ], tCancel );
			await GitAsync( [ "-C", tempDirectory, "update-ref", "refs/cpr/approved", tempCommit ], tCancel );
			string tempBundle = Path.Combine( tempDirectory, "approved-" + Guid.NewGuid().ToString( "N" ) + ".bundle" );
			try
			{
				await GitAsync( [ "-C", tempDirectory, "bundle", "create", tempBundle, "refs/cpr/approved" ], tCancel );
				tempRequest = tempRequest with { ApprovedCommit = tempCommit };
				string tempDestination = ReadLine( await RunAsync( tDestination, "prepare-import", tempRequest, 120, tCancel ), "CPR_IMPORT_PATH" );
				if ( !Regex.IsMatch( tempDestination, "^/[A-Za-z0-9_./-]+$" ) || tempDestination.Split( '/' ).Contains( ".." ) )
				{
					throw new InvalidOperationException( "Invalid worker import path" );
				}

				CommandResult tempCopy = await tCommand.RunAsync( "scp", [ "-O", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", tempBundle, tDestination.SshHost + ":" + tempDestination ], tTimeoutSeconds: 0, tCancel: tCancel );
				if ( !tempCopy.IsSuccess )
				{
					throw new ReviewFailureException( "infrastructure", "Approved workspace transfer failed: " + tempCopy.Failure );
				}

				string tempImported = ReadLine( await RunAsync( tDestination, "import-approved", tempRequest, 0, tCancel ), "CPR_IMPORTED" );
				if ( tempImported != tempCommit )
				{
					throw new InvalidOperationException( "Destination did not confirm the approved commit" );
				}
			}
			finally
			{
				File.Delete( tempBundle );
			}
		}

		public Task ParkAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			return RunAsync( tWorker, "park", Request( tRequest ) with { IsPreserved = true }, 120, tCancel );
		}

		public Task DetachAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			return RunAsync( tWorker, "detach", Request( tRequest ), 120, tCancel );
		}

		public async Task CleanupAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, "cleanup", Request( tRequest ), 120, tCancel );
			RequireMarker( tempOutput, "CPR_CLEANED" );
		}

		public async Task<JsonElement[]> ReadHistoryAsync( Worker tWorker, PullRequest tRequest, Dictionary<string, string> tKnown, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, "history", Request( tRequest ) with { KnownHistory = tKnown }, 120, tCancel );
			RequireMarker( tempOutput, "CPR_HISTORY_DONE" );

			return [ .. tempOutput.Split( '\n' ).Where( x => x.StartsWith( "CPR_HISTORY|", StringComparison.Ordinal ) ).Select( x => JsonSerializer.Deserialize<JsonElement>( Convert.FromBase64String( x[ "CPR_HISTORY|".Length.. ].Trim() ) ) ) ];
		}

		public async Task<WorkerReviewState> InspectReviewAsync( Worker tWorker, PullRequest tRequest, string tOperationId, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, "inspect", Request( tRequest ) with { OperationId = tOperationId }, 60, tCancel );

			return JsonSerializer.Deserialize<WorkerReviewState>( Convert.FromBase64String( ReadLine( tempOutput, "CPR_STATE" ) ) ) ?? throw new InvalidOperationException( "Missing worker recovery state" );
		}

		public async Task CancelReviewAsync( Worker tWorker, PullRequest tRequest, string tOperationId, CancellationToken tCancel )
		{
			string tempOutput = await RunAsync( tWorker, "cancel", Request( tRequest ) with { OperationId = tOperationId }, 45, tCancel );
			RequireMarker( tempOutput, "CPR_CANCELLED" );
		}

		private WorkerRequest Request( PullRequest tRequest )
		{
			return new( 1, tRequest.PullRequestId, tRequest.Repository, tRequest.Number, tRequest.TargetBranch, _settings.GitAuthorName, _settings.GitAuthorEmail );
		}

		private async Task<string> GitAsync( IEnumerable<string> tArguments, CancellationToken tCancel )
		{
			CommandResult tempResult = await tCommand.RunAsync( "git", tArguments, tTimeoutSeconds: 0, tCancel: tCancel );
			if ( !tempResult.IsSuccess )
			{
				throw new ReviewFailureException( "infrastructure", "Approved workspace Git transfer failed: " + tempResult.Failure );
			}

			return tempResult.Output.Trim();
		}

		private async Task<string> RunAsync( Worker tWorker, string tOperation, WorkerRequest tRequest, int tTimeoutSeconds, CancellationToken tCancel )
		{
			// The operation is an internal constant; all request data travels through stdin
			CommandResult tempResult = await tCommand.RunAsync( "ssh", [ "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-T", tWorker.SshHost, "cpr-worker " + tOperation ], tInput: JsonSerializer.Serialize( tRequest ), tTimeoutSeconds: tTimeoutSeconds, tCancel: tCancel );
			if ( !tempResult.IsSuccess )
			{
				string tempFailure = tempResult.Failure;
				string tempCategory = ReviewFailureException.IsTransient( tempFailure ) ? "transient" : "infrastructure";
				string? tempLine = ( tempResult.Error + "\n" + tempResult.Output ).Split( '\n' ).LastOrDefault( x => x.StartsWith( "CPR_FAILURE|", StringComparison.Ordinal ) );
				if ( tempLine is not null )
				{
					using JsonDocument tempData = JsonDocument.Parse( Convert.FromBase64String( tempLine[ "CPR_FAILURE|".Length.. ].Trim() ) );
					tempCategory = tempData.RootElement.GetProperty( "category" ).GetString()!;
					tempFailure = tempData.RootElement.GetProperty( "action" ).GetString()!;
				}
				else if ( tOperation == "run" && string.IsNullOrWhiteSpace( tempFailure ) )
				{
					tempCategory = "interrupted";
					tempFailure = $"Worker exited {tempResult.ExitCode} without diagnostics. Check the registered operation and saved checkout before resuming.";
				}

				throw new ReviewFailureException( tempCategory, $"Worker {tWorker.WorkerId} {tOperation} failed: {tempFailure}" );
			}

			return tempResult.Output;
		}

		private static string ReadLine( string tOutput, string tMarker )
		{
			string tempPrefix = tMarker + "|";
			string tempLine = tOutput.Split( '\n' ).LastOrDefault( x => x.StartsWith( tempPrefix, StringComparison.Ordinal ) ) ?? throw new InvalidOperationException( "Missing worker response: " + tMarker );

			return tempLine[ tempPrefix.Length.. ].Trim();
		}

		private static void RequireMarker( string tOutput, string tMarker )
		{
			if ( !tOutput.Split( '\n' ).Any( x => x.Trim() == tMarker ) )
			{
				throw new InvalidOperationException( "Worker did not confirm " + tMarker );
			}
		}

		private static WorkerSnapshot ReadSnapshot( string tOutput )
		{
			WorkerSnapshot tempSnapshot = JsonSerializer.Deserialize<WorkerSnapshot>( Convert.FromBase64String( ReadLine( tOutput, "CPR_SNAPSHOT" ) ) ) ?? throw new InvalidOperationException( "Missing worker snapshot" );
			if ( !Regex.IsMatch( tempSnapshot.TreeSha, "^[a-f0-9]{40}$" ) || !Regex.IsMatch( tempSnapshot.TargetSha, "^[a-f0-9]{40}$" ) || !Regex.IsMatch( tempSnapshot.RunKey, "^[A-Za-z0-9_-]{1,128}$" ) || ( tempSnapshot.SessionId is not null && !Regex.IsMatch( tempSnapshot.SessionId, "^[A-Za-z0-9_-]{6,128}$" ) ) )
			{
				throw new InvalidOperationException( "Invalid worker snapshot" );
			}

			return tempSnapshot;
		}
	}
}
