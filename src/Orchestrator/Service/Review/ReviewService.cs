using CPR.Service.GitHub;
using CPR.Service.Queue;
using CPR.Service.SSH;
using CPR.SQL;

namespace CPR.Service.Review
{
	public sealed class ReviewService( GitHubClient tGitHub, WorkerClient tWorkers, ReviewHistoryService tHistory, QueueGate tGate, IServiceScopeFactory tScopes, IOptions<Settings> tSettings, ILogger<ReviewService> tLogger )
	{
		public async Task<WorkerSnapshot> ExecuteAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			Exception? tempFailure = null;
			ReviewRecovery tempRecovery = ReviewRecovery.Read( tRequest );
			ReviewOperation tempOperation = new( tempRecovery.OperationId ?? Guid.NewGuid().ToString( "N" ) );
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();

				if ( !await tempContext.PullRequest.AnyAsync( x => x.PullRequestId == tRequest.PullRequestId && x.Status == PullRequestStatus.Processing && x.WorkerId == tWorker.WorkerId, tCancel ) )
				{
					throw new InvalidOperationException( "PR state changed before review started" );
				}

				PullRequest tempCurrent = await tempContext.PullRequest.SingleAsync( x => x.PullRequestId == tRequest.PullRequestId, tCancel );
				tempRecovery.OperationId = tempOperation.Id;
				tempRecovery.IsPending = false;
				tempRecovery.NextAttemptAt = null;
				tempRecovery.Action = tempRecovery.IsContinuation ? "Resuming missing review work and revalidating affected changes" : "Review in progress";
				tempRecovery.Save( tempCurrent );
				tempRecovery.Save( tRequest );
				await tempContext.SaveChangesAsync( tCancel );
				tGate.Reviews.Add( tRequest.PullRequestId, tempOperation );
			}
			finally
			{
				tGate.Mutex.Release();
			}

			try
			{
				WorkerSnapshot tempResult = await ExecuteCoreAsync( tWorker, tRequest, tempOperation.Id, false, tCancel );
				GitHubPullRequest tempCurrent = await tGitHub.ReadAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tCancel );

				if ( tempResult.IsHistorical )
				{
					throw new InvalidOperationException( $"Saved review report recovered for target {tempResult.TargetSha}; current work still needs integration. Use cpr connect {tRequest.PullRequestId}; no full review is required" );
				}

				if ( !WorkflowRules.IsSameSource( tRequest, tempCurrent ) || ( tRequest.ApprovedSha is not null && tempCurrent.TargetSha != tempResult.TargetSha ) )
				{
					throw new InvalidOperationException( "PR head or target changed during review; use cpr connect for manual target integration, or request a fresh review" );
				}

				return tempResult;
			}
			catch ( Exception tException )
			{
				tempFailure = tException;

				throw;
			}
			finally
			{
				try
				{
					using CancellationTokenSource tempCapture = new( TimeSpan.FromSeconds( 90 ) );
					await tHistory.CaptureAsync( tWorker, tRequest, tempCapture.Token );
				}
				catch ( Exception tException ) when ( tempFailure is not null )
				{
					tLogger.LogWarning( tException, "History capture for PR {Id} remains pending after its failed operation", tRequest.PullRequestId );
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

		public async Task<string?> PrepareInteractiveAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			return await tWorkers.PrepareInteractiveAsync( tWorker, tRequest, tCancel );
		}

		private async Task<ReviewReport> EnsureFullReportAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			using IServiceScope tempScope = tScopes.CreateScope();
			Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
			ReviewReport? tempReport = await ReviewComment.FindFullReportAsync( tempContext, tRequest, tCancel );

			if ( tempReport is null )
			{
				try
				{
					await ExecuteCoreAsync( tWorker, tRequest, Guid.NewGuid().ToString( "N" ), true, tCancel );
				}
				finally
				{
					using CancellationTokenSource tempCapture = new( TimeSpan.FromSeconds( 90 ) );
					await tHistory.CaptureAsync( tWorker, tRequest, tempCapture.Token );
				}

				tempReport = await ReviewComment.GetFullReportAsync( tempContext, tRequest, tCancel );
			}

			return tempReport;
		}

		public async Task<WorkerSnapshot> CompleteTweakAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			GitHubPullRequest tempRemote = await tGitHub.ReadAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tCancel );

			if ( !WorkflowRules.IsSameSource( tRequest, tempRemote ) )
			{
				throw new InvalidOperationException( "PR head or destination changed; request a fresh review before completing the manual merge" );
			}

			try
			{
				WorkerSnapshot tempResult = await tWorkers.CompleteTweakAsync( tWorker, tRequest, tempRemote.TargetSha, tCancel );
				GitHubPullRequest tempCurrent = await tGitHub.ReadAsync( tempRemote.Url, tCancel );

				if ( !WorkflowRules.IsSameSource( tRequest, tempCurrent ) || tempCurrent.TargetSha != tempResult.TargetSha )
				{
					throw new InvalidOperationException( "PR head or target changed during manual completion; use cpr connect again to integrate the latest target" );
				}

				return tempResult;
			}
			finally
			{
				using CancellationTokenSource tempCapture = new( TimeSpan.FromSeconds( 90 ) );
				await tHistory.CaptureAsync( tWorker, tRequest, tempCapture.Token );
			}
		}

		private async Task<WorkerSnapshot> ExecuteCoreAsync( Worker tWorker, PullRequest tRequest, string tOperationId, bool tIsHistoricalReport, CancellationToken tCancel )
		{
			bool tempIsReportRetry = tIsHistoricalReport || tRequest.WorkReason == PullRequestWorkReason.ReportRetry;
			ReviewStepSettings[] tempSettings = tempIsReportRetry ? [] : [ .. tSettings.Value.ReviewSteps.Where( x => x.IsEnabled ) ];
			Dictionary<PromptKind, string> tempPrompts = tSettings.Value.Prompts;
			bool tempIsMergeRepair = !tempIsReportRetry && tRequest.WorkReason == PullRequestWorkReason.MergeConflict && ( tRequest.PublishedSha is not null || tRequest.ApprovedSha is not null );
			bool tempIsFeedbackRepair = !tempIsReportRetry && tRequest.WorkReason == PullRequestWorkReason.FeedbackRepair;
			ReviewReport? tempPriorReview = null;
			FeedbackComment[] tempFeedback = tempIsFeedbackRepair ? JsonSerializer.Deserialize<FeedbackState>( tRequest.ReviewFeedback )!.Active : [];

			if ( tempIsFeedbackRepair )
			{
				if ( !tSettings.Value.FeedbackRepairStep.IsEnabled || tempFeedback.Length == 0 )
				{
					throw new InvalidOperationException( "CodeRabbit repair is disabled or its captured feedback batch is missing" );
				}

				tempPriorReview = await EnsureFullReportAsync( tWorker, tRequest, tCancel );
				tempSettings = [ tSettings.Value.FeedbackRepairStep ];
			}

			if ( tempIsMergeRepair )
			{
				tempPriorReview = await EnsureFullReportAsync( tWorker, tRequest, tCancel );

				if ( !tSettings.Value.MergeRepairStep.IsEnabled )
				{
					throw new InvalidOperationException( "Automatic merge repair is disabled; use cpr connect to resolve target integration" );
				}

				tempSettings = [ tSettings.Value.MergeRepairStep ];
			}

			bool tempIsFocusedRepair = tempIsMergeRepair || tempIsFeedbackRepair;
			List<ReviewStep> tempSteps = [];
			int tempCount = tempSettings.Length;

			for ( int i = 0; i < tempCount; ++i )
			{
				ReviewStepSettings tempStep = tempSettings[ i ];
				IEnumerable<PromptKind> tempKinds = tempIsFocusedRepair ? tempStep.Prompts : [ PromptKind.Review, PromptKind.Relevance, .. tempStep.Prompts, PromptKind.Evidence ];

				if ( !tempIsFocusedRepair && i == tempCount - 1 )
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

				tempSteps.Add( new( tempStep.Name, tempStep.Model, tempStep.ReasoningEffort, tempStep.MaxAttempts, tempPrompt ) );
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
				tempContext = await tGitHub.ReadReviewContextAsync( $"https://github.com/{tRequest.Repository}/pull/{tRequest.Number}", tIsFocused: tempIsFocusedRepair, tCancel );
			}

			ReviewStepSettings tempRecoverySettings = tSettings.Value.RecoveryStep;
			ReviewStep? tempRecovery = tempRecoverySettings.IsEnabled ? new( tempRecoverySettings.Name, tempRecoverySettings.Model, tempRecoverySettings.ReasoningEffort, 1, PromptRenderer.Render( tempPrompts[ PromptKind.ReportRecovery ], tRequest ) ) : null;
			ReviewStepSettings tempValidation = tSettings.Value.ReviewSteps.Last( x => x.IsEnabled );
			ReviewStep tempSavedValidation = new( "saved-validation", tempValidation.Model, "low", 1, PromptRenderer.Render( tempPrompts[ PromptKind.SavedValidation ], tRequest ) );
			ReviewStep? tempHeadReassessment = null;

			if ( !tempIsReportRetry && tRequest.WorkReason == PullRequestWorkReason.Rereview && tRequest.PublishedSha is null && !ReviewRecovery.Read( tRequest ).IsFullReviewRequested )
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				tempPriorReview = await ReviewComment.FindFullReportAsync( tempScope.ServiceProvider.GetRequiredService<Context>(), tRequest, tCancel );

				if ( tempPriorReview is not null && tempPriorReview.HeadSha != tRequest.RemoteSha )
				{
					const string tempAssignment = "Reassess the changed PR head in a single final pass. The scope inventory includes source and target deltas, differences from the prior reviewed tree, and any repairs you make. Reconcile archived unpublished repairs, inspect changed functions and affected callers, and run the relevant final checks. Use the original review only as historical evidence for inputs you verify still match, and record any changed evidence boundaries. Assess relevance, correctness, cleanup, optimization, memory and the final diff within this scope. Explain what changed since the prior full review, what was reused, and any remaining limitations in pr_comment.";
					string tempFocusedPrompt = string.Join( "\n\n", new[] { PromptKind.Correctness, PromptKind.Cleanup, PromptKind.Evidence }.Select( x => PromptRenderer.Render( tempPrompts[ x ], tRequest ) ) );
					tempHeadReassessment = new( "head-reassessment", tempValidation.Model, tempValidation.ReasoningEffort, 1, tempAssignment + "\n\n" + tempFocusedPrompt );
				}
			}

			ReviewPlan tempPlan = new
			(
				HeadSha: tRequest.RemoteSha,
				IsRereview: tRequest.WorkReason == PullRequestWorkReason.Rereview,
				Context: tempContext,
				Steps: [ .. tempSteps ],
				OperationId: tOperationId,
				MaintainerResolution: tRequest.MaintainerResolution,
				PublishedSha: tRequest.PublishedSha,
				IsReportRetry: tempIsReportRetry,
				IsMergeRepair: tempIsMergeRepair,
				PriorReview: tempPriorReview,
				IsHistoricalReport: tIsHistoricalReport,
				SavedValidationStep: tempSavedValidation,
				RecoveryStep: tempRecovery,
				IsFeedbackRepair: tempIsFeedbackRepair,
				Feedback: tempFeedback,
				ApprovedTreeSha: tempIsReportRetry ? null : tRequest.ApprovedSha,
				IsContinuation: ReviewRecovery.Read( tRequest ).IsContinuation,
				RecoveryAttempt: ReviewRecovery.Read( tRequest ).Attempts,
				HeadReassessmentStep: tempHeadReassessment
			);

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

			ValidateStep( tSettings.FeedbackRepairStep, [ PromptKind.FeedbackRepair, PromptKind.Correctness, PromptKind.Evidence ] );

			if ( tSettings.CodeRabbitPollSeconds is < 5 or > 300 || tSettings.CodeRabbitQuietSeconds is < 10 or > 300 || tSettings.CodeRabbitFallbackQuietSeconds is < 120 or > 900 || tSettings.FeedbackRepairStep.MaxAttempts != 1 || !tSettings.FeedbackRepairStep.Prompts.Contains( PromptKind.FeedbackRepair ) || !tSettings.FeedbackRepairStep.Prompts.Contains( PromptKind.Evidence ) )
			{
				throw new InvalidOperationException( "CodeRabbit requires bounded polling, settling delays, and one focused repair with feedback and evidence prompts" );
			}

			ValidateStep( tSettings.RecoveryStep, [ PromptKind.ReportRecovery ] );

			if ( tSettings.RecoveryStep.MaxAttempts != 1 )
			{
				throw new InvalidOperationException( "RecoveryStep permits one attempt" );
			}

			ValidateStep( tSettings.MergeRepairStep, [ PromptKind.MergeRepair, PromptKind.Correctness ] );

			if ( !tSettings.MergeRepairStep.Prompts.Contains( PromptKind.MergeRepair ) )
			{
				throw new InvalidOperationException( "MergeRepairStep requires the scoped MergeRepair prompt" );
			}
		}

		private static void ValidateStep( ReviewStepSettings? tStep, PromptKind[] tAllowedPrompts )
		{
			if ( tStep is null || string.IsNullOrEmpty( tStep.Name ) || !Regex.IsMatch( tStep.Name, "^[a-z][a-z0-9-]{0,39}$" ) || string.IsNullOrEmpty( tStep.Model ) || !Regex.IsMatch( tStep.Model, "^[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}$" ) || tStep.ReasoningEffort is not ( "low" or "medium" or "high" or "xhigh" or "max" or "ultra" ) || tStep.MaxAttempts is < 1 or > 3 || tStep.Prompts is null || tStep.Prompts.Length == 0 || tStep.Prompts.Any( x => !tAllowedPrompts.Contains( x ) ) )
			{
				throw new InvalidOperationException( $"Invalid CPR review step '{tStep?.Name}': check its name, model, effort, attempts, and prompt kinds" );
			}
		}
	}
}
