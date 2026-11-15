using CPR.Service.GitHub;
using CPR.Service.Queue;
using CPR.Service.SSH;
using CPR.SQL;

namespace CPR.Service.Review
{
	public sealed class ReviewService( GitHubClient tGitHub, WorkerClient tWorkers, ReviewHistoryService tHistory, QueueGate tGate, IServiceScopeFactory tScopes, IOptions<Settings> tSettings )
	{
		public async Task<WorkerSnapshot> ExecuteAsync( Worker tWorker, PullRequest tRequest, bool tIsValidationOnly, CancellationToken tCancel )
		{
			ReviewOperation tempOperation = new( Guid.NewGuid().ToString( "N" ) );
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();

				if ( !await tempContext.PullRequest.AnyAsync( x => x.PullRequestId == tRequest.PullRequestId && x.Status == PullRequestStatus.Processing && x.WorkerId == tWorker.WorkerId, tCancel ) )
				{
					throw new InvalidOperationException( "PR state changed before review started" );
				}

				tGate.Reviews.Add( tRequest.PullRequestId, tempOperation );
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				WorkerSnapshot tempResult = await ExecuteCoreAsync( tWorker, tRequest, tIsValidationOnly, tempOperation.Id, tCancel );
				GitHubPullRequest tempCurrent = await tGitHub.ReadAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tCancel );

				if ( !WorkflowRules.IsSameSource( tRequest, tempCurrent ) || tempCurrent.TargetSha != tempResult.TargetSha )
				{
					throw new InvalidOperationException( "PR head or target changed during review; use cpr connect --tweak for manual target integration, or request a fresh review" );
				}

				return tempResult;
			}
			finally
			{
				try
				{
					using CancellationTokenSource tempCapture = new( TimeSpan.FromSeconds( 90 ) );
					await tHistory.CaptureAsync( tWorker, tRequest, tempCapture.Token );
				}
				finally
				{
					await tGate.Mutex.WaitAsync( CancellationToken.None );

					try
					{
						tGate.Reviews.Remove( tRequest.PullRequestId );
					}
					finally
					{
						tGate.Mutex.Release();
					}
				}
			}
		}

		public async Task<WorkerSnapshot> CompleteTweakAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			GitHubPullRequest tempRemote = await tGitHub.ReadAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tCancel );

			if ( !WorkflowRules.IsSameSource( tRequest, tempRemote ) )
			{
				throw new InvalidOperationException( "PR head or destination changed; request a fresh review before completing the manual merge" );
			}

			if ( tRequest.PublishedSha is null )
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				await ReviewComment.GetFullReportAsync( tempContext, tRequest, tCancel );
			}

			try
			{
				WorkerSnapshot tempResult = await tWorkers.CompleteTweakAsync( tWorker, tRequest, tempRemote.TargetSha, tCancel );
				GitHubPullRequest tempCurrent = await tGitHub.ReadAsync( tempRemote.Url, tCancel );

				if ( !WorkflowRules.IsSameSource( tRequest, tempCurrent ) || tempCurrent.TargetSha != tempResult.TargetSha )
				{
					throw new InvalidOperationException( "PR head or target changed during manual completion; use cpr connect --tweak again to integrate the latest target" );
				}

				return tempResult;
			}
			finally
			{
				using CancellationTokenSource tempCapture = new( TimeSpan.FromSeconds( 90 ) );
				await tHistory.CaptureAsync( tWorker, tRequest, tempCapture.Token );
			}
		}

		private async Task<WorkerSnapshot> ExecuteCoreAsync( Worker tWorker, PullRequest tRequest, bool tIsValidationOnly, string tOperationId, CancellationToken tCancel )
		{
			bool tempIsReportRetry = tRequest.WorkReason == PullRequestWorkReason.ReportRetry;
			ReviewStepSettings[] tempSettings = tempIsReportRetry ? [] : tIsValidationOnly ? [ tSettings.Value.ReviewSteps.Last( x => x.IsEnabled ) ] : [ .. tSettings.Value.ReviewSteps.Where( x => x.IsEnabled ) ];
			Dictionary<PromptKind, string> tempPrompts = tSettings.Value.Prompts;
			bool tempIsMergeRepair = !tIsValidationOnly && tRequest.WorkReason == PullRequestWorkReason.MergeConflict && tRequest.PublishedSha is not null;
			ReviewReport? tempPriorReview = null;

			if ( tempIsMergeRepair )
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempDatabase = tempScope.ServiceProvider.GetRequiredService<Context>();
				tempPriorReview = await ReviewComment.GetFullReportAsync( tempDatabase, tRequest, tCancel );

				if ( !tSettings.Value.MergeRepairStep.IsEnabled )
				{
					throw new InvalidOperationException( "Automatic merge repair is disabled; use cpr connect --tweak to resolve target integration" );
				}

				tempSettings = [ tSettings.Value.MergeRepairStep ];
			}

			PromptKind tempOperation = tRequest.WorkReason switch
			{
				PullRequestWorkReason.MergeConflict => PromptKind.MergeConflict,
				PullRequestWorkReason.Rereview => PromptKind.Rereview,
				_ => PromptKind.InitialReview
			};
			List<ReviewStep> tempSteps = [];
			int tempCount = tempSettings.Length;

			for ( int i = 0; i < tempCount; ++i )
			{
				ReviewStepSettings tempStep = tempSettings[ i ];
				IEnumerable<PromptKind> tempKinds = tempIsMergeRepair ? tempStep.Prompts : [ PromptKind.SharedReview, PromptKind.Relevance, tIsValidationOnly ? PromptKind.FinalValidation : tempOperation, .. tempStep.Prompts, PromptKind.Evidence ];

				if ( !tempIsMergeRepair && i == tempCount - 1 )
				{
					tempKinds = tempKinds.Concat( [ PromptKind.Correctness, PromptKind.Resources, PromptKind.Cleanup, PromptKind.FinalValidation ] );
				}

				List<string> tempBlocks = [];

				foreach ( PromptKind tempKind in tempKinds.Distinct() )
				{
					if ( !tempPrompts.TryGetValue( tempKind, out string? tempContent ) || string.IsNullOrWhiteSpace( tempContent ) )
					{
						throw new InvalidOperationException( $"Set CPR:Prompts:{tempKind} in application configuration" );
					}

					tempBlocks.Add( $"## {tempKind}\n{PromptRenderer.Render( tempContent, tRequest )}" );
				}

				string tempPrompt = string.Join( "\n\n", tempBlocks );

				if ( Regex.IsMatch( tempPrompt, @"\{\{[A-Za-z][A-Za-z0-9]*\}\}" ) )
				{
					throw new InvalidOperationException( $"Unexpanded token in review step {tempStep.Name}" );
				}

				tempSteps.Add( new( tempStep.Name, tempStep.Model, tempStep.ReasoningEffort, tempStep.TimeoutSeconds, tempStep.MaxAttempts, tempPrompt ) );
			}

			JsonElement tempContext;

			if ( tempIsReportRetry )
			{
				GitHubPullRequest tempRemote = await tGitHub.ReadAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tCancel );

				if ( !WorkflowRules.IsSameSource( tRequest, tempRemote ) )
				{
					throw new InvalidOperationException( "PR head or destination changed; report retry cannot renew review coverage" );
				}

				tempContext = JsonSerializer.SerializeToElement( new { tempRemote.TargetSha, PullRequest = new { head = new { sha = tempRemote.HeadSha }, state = "open", @base = new { @ref = tempRemote.TargetBranch } } } );
			}
			else
			{
				tempContext = await tGitHub.ReadReviewContextAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tCancel );
			}

			ReviewStepSettings tempRepairSettings = tSettings.Value.ReportRepairStep;
			ReviewStep? tempReportRepair = tempRepairSettings.IsEnabled ? new( tempRepairSettings.Name, tempRepairSettings.Model, tempRepairSettings.ReasoningEffort, tempRepairSettings.TimeoutSeconds, tempRepairSettings.MaxAttempts, PromptRenderer.Render( tempPrompts[ PromptKind.ReportRepair ], tRequest ) ) : null;
			ReviewPlan tempPlan = new( tRequest.RemoteSha, tIsValidationOnly || tempIsReportRetry, tRequest.WorkReason == PullRequestWorkReason.Rereview, tempContext, [ .. tempSteps ], tOperationId, tRequest.MaintainerResolution, tRequest.PublishedSha, tempIsReportRetry, tempReportRepair, tempIsMergeRepair, tempPriorReview );

			return await tWorkers.ExecuteAsync( tWorker, tRequest, tempPlan, tCancel );
		}

		public static void ValidateSettings( Settings tSettings )
		{
			if ( tSettings.Prompts is null || tSettings.Prompts.Keys.Any( x => !Enum.IsDefined( x ) ) )
			{
				throw new InvalidOperationException( "CPR:Prompts must contain named review prompts" );
			}

			foreach ( PromptKind tempKind in Enum.GetValues<PromptKind>() )
			{
				if ( !tSettings.Prompts.TryGetValue( tempKind, out string? tempContent ) || string.IsNullOrWhiteSpace( tempContent ) || tempContent.Length > 30000 )
				{
					throw new InvalidOperationException( $"CPR:Prompts:{tempKind} must be nonempty and <= 30000 characters" );
				}
			}

			ReviewStepSettings[] tempConfiguredSteps = tSettings.ReviewSteps;

			if ( tempConfiguredSteps is null || tempConfiguredSteps.Any( x => x is null ) )
			{
				throw new InvalidOperationException( "CPR:ReviewSteps must contain valid step settings" );
			}

			ReviewStepSettings[] tempSteps = [ .. tempConfiguredSteps.Where( x => x.IsEnabled ) ];

			if ( tempSteps.Length is < 1 or > 8 || tempSteps.Select( x => x.Name ).Distinct( StringComparer.Ordinal ).Count() != tempSteps.Length )
			{
				throw new InvalidOperationException( "CPR:ReviewSteps requires one to eight uniquely named steps" );
			}

			foreach ( ReviewStepSettings tempStep in tempSteps )
			{
				ValidateStep( tempStep, [ PromptKind.Correctness, PromptKind.Resources, PromptKind.Cleanup, PromptKind.FinalValidation, PromptKind.Relevance ] );
			}

			ValidateStep( tSettings.ReportRepairStep, [ PromptKind.ReportRepair ] );
			ValidateStep( tSettings.MergeRepairStep, [ PromptKind.MergeRepair, PromptKind.Correctness ] );

			if ( !tSettings.MergeRepairStep.Prompts.Contains( PromptKind.MergeRepair ) )
			{
				throw new InvalidOperationException( "MergeRepairStep requires the scoped MergeRepair prompt" );
			}
		}

		private static void ValidateStep( ReviewStepSettings? tStep, PromptKind[] tAllowedPrompts )
		{
			if ( tStep is null || string.IsNullOrEmpty( tStep.Name ) || !Regex.IsMatch( tStep.Name, "^[a-z][a-z0-9-]{0,39}$" ) || string.IsNullOrEmpty( tStep.Model ) || !Regex.IsMatch( tStep.Model, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$" ) || tStep.ReasoningEffort is not ( "low" or "medium" or "high" or "xhigh" or "max" or "ultra" ) || tStep.TimeoutSeconds is < 60 or > 7200 || tStep.MaxAttempts is < 1 or > 3 || tStep.Prompts is null || tStep.Prompts.Length == 0 || tStep.Prompts.Any( x => !tAllowedPrompts.Contains( x ) ) )
			{
				throw new InvalidOperationException( $"Invalid CPR review step '{tStep?.Name}': check its name, model, effort, timeout, attempts, and prompt kinds" );
			}
		}
	}
}
