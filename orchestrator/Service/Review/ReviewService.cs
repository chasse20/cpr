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
					throw new InvalidOperationException( "PR head or target changed during review; request a fresh review" );
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

		private async Task<WorkerSnapshot> ExecuteCoreAsync( Worker tWorker, PullRequest tRequest, bool tIsValidationOnly, string tOperationId, CancellationToken tCancel )
		{
			ReviewStepSettings[] tempSettings = [ .. tSettings.Value.ReviewSteps.Where( x => x.IsEnabled ) ];
			Dictionary<PromptKind, string> tempPrompts = tSettings.Value.Prompts;
			PromptKind tempOperation = tRequest.WorkReason switch
			{
				PullRequestWorkReason.MergeConflict => PromptKind.MergeConflict,
				PullRequestWorkReason.Rereview => PromptKind.Rereview,
				_ => PromptKind.InitialReview
			};
			List<ReviewStep> tempSteps = [];
			int tempCount = tempSettings.Length;

			for ( int i = tIsValidationOnly ? tempCount - 1 : 0; i < tempCount; ++i )
			{
				ReviewStepSettings tempStep = tempSettings[ i ];
				IEnumerable<PromptKind> tempKinds = [ PromptKind.SharedReview, PromptKind.Relevance, tIsValidationOnly ? PromptKind.FinalValidation : tempOperation, .. tempStep.Prompts ];

				if ( i == tempCount - 1 )
				{
					tempKinds = tempKinds.Concat( [ PromptKind.Correctness, PromptKind.Resources, PromptKind.Cleanup, PromptKind.FinalValidation ] );
				}

				List<string> tempBlocks = [];

				foreach ( PromptKind tempKind in tempKinds.Append( PromptKind.Evidence ).Distinct() )
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

			JsonElement tempContext = await tGitHub.ReadReviewContextAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tCancel );
			ReviewPlan tempPlan = new( tRequest.RemoteSha, tIsValidationOnly, tRequest.WorkReason == PullRequestWorkReason.Rereview, tempContext, [ .. tempSteps ], tOperationId, tRequest.MaintainerResolution );

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
				if ( string.IsNullOrEmpty( tempStep.Name ) || !Regex.IsMatch( tempStep.Name, "^[a-z][a-z0-9-]{0,39}$" ) || string.IsNullOrEmpty( tempStep.Model ) || !Regex.IsMatch( tempStep.Model, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$" ) || tempStep.ReasoningEffort is not ( "low" or "medium" or "high" or "xhigh" or "max" or "ultra" ) || tempStep.TimeoutSeconds is < 60 or > 7200 || tempStep.MaxAttempts is < 1 or > 3 || tempStep.Prompts is null || tempStep.Prompts.Length == 0 || tempStep.Prompts.Any( x => x is not ( PromptKind.Correctness or PromptKind.Resources or PromptKind.Cleanup or PromptKind.FinalValidation or PromptKind.Relevance ) ) )
				{
					throw new InvalidOperationException( $"Invalid CPR review step '{tempStep.Name}': check its name, model, effort, timeout, attempts, and prompt kinds" );
				}
			}
		}
	}
}
