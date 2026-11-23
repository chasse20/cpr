using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;
using CPR.Worker.Recovery;
using CPR.Worker.Reports;

namespace CPR.Worker.Review
{
	public sealed class ReviewRunner( ReviewExecution tExecution )
	{
		private readonly ReviewExecution _review = tExecution;

		public async Task<WorkerSnapshot> RunAsync( CancellationToken tCancel )
		{
			int tempCode = 13;
			string tempCategory = "infrastructure";
			string tempAction = "Worker stopped before completion; inspect the retained run";
			try
			{
				_review.Model.RequireNotCancelled();
				await PrepareReviewAsync( tCancel );
				if ( _review.Plan.IsReportRetry )
				{
					await new ReportRetry( _review ).RunAsync( tCancel );
				}
				else
				{
					int tempStart = await new ReviewContinuation( _review ).ResumeAsync( tCancel );
					for ( int i = tempStart, tempCount = _review.Plan.Steps.Length; i < tempCount; ++i )
					{
						await RunStepAsync( i, tCancel );
					}
				}
				await _review.RequireUnchangedAsync( tCancel );
				ReviewResult tempResult = Storage.Read<ReviewResult>( Path.Combine( _review.Attempt, "result.json" ), true );
				Storage.Write( Path.Combine( _review.Run, "result.json" ), tempResult, true );
				Storage.WriteText( Path.Combine( _review.Run, "report.md" ), ReportWriter.Render( tempResult ) );
				await _review.Workspace.WriteGitAsync( Path.Combine( _review.Run, "changes.patch" ), [ "diff", "--binary", _review.Head, _review.OutputTree ], tCancel );
				await _review.Workspace.WriteGitAsync( Path.Combine( _review.Run, "changes.stat" ), [ "diff", "--stat", _review.Head, _review.OutputTree ], tCancel );
				Storage.Update( _review.Manifest, x =>
				{
					x[ "reviewed_tree" ] = _review.OutputTree;
					x[ "session_id" ] = _review.Session;
					x[ "is_historical" ] = _review.IsHistorical;
					if ( _review.IsFeedbackRepair )
					{
						x[ "feedback_dispositions" ] = JsonNode.Parse( File.ReadAllText( Storage.Evidence( _review.Artifacts, "evidence/feedback-dispositions.json" ) ) );
					}
				} );
				WorkerSnapshot tempSnapshot = new( _review.OutputTree, _review.Session.Length > 0 ? _review.Session : null, _review.Target, Path.GetFileName( _review.Run ), _review.IsHistorical );
				if ( !_review.IsHistorical )
				{
					await _review.Workspace.SnapshotAsync( tempSnapshot, _review.Run, tCancel );
				}
				tempCode = 0;

				return tempSnapshot;
			}
			catch ( WorkerFailure tException )
			{
				tempCode = tException.ExitCode;
				tempCategory = tException.Category;
				tempAction = tException.Message;
				throw;
			}
			catch ( OperationCanceledException )
			{
				tempCode = 143;
				tempCategory = "interrupted";
				tempAction = "Review execution was interrupted; reconcile the worker and resume missing work";
				throw;
			}
			catch ( JsonException tException )
			{
				tempCategory = "report_metadata";
				tempAction = "Invalid structured report; inspect the retained attempt. " + tException.Message;
				throw new WorkerFailure( tempCategory, tempAction );
			}
			catch ( Exception tException )
			{
				tempAction = tException.Message;
				throw;
			}
			finally
			{
				bool tempIsCancelled = File.Exists( Path.Combine( _review.Workspace.Job, "cancel-" + _review.Plan.OperationId ) );
				if ( tempIsCancelled )
				{
					tempCategory = "cancelled";
					tempAction = "Cancelled by user; use connect or review to continue";
				}
				Storage.Update( _review.Manifest, x =>
				{
					x[ "status" ] = tempIsCancelled ? "cancelled" : tempCode == 0 ? "complete" : "failed";
					x[ "completed_at" ] = DateTimeOffset.UtcNow;
					x[ "exit_code" ] = tempCode;
					if ( tempCode != 0 )
					{
						x[ "failure" ] = new JsonObject { [ "category" ] = tempCategory, [ "action" ] = tempAction };
					}
				} );
			}
		}

		private async Task PrepareReviewAsync( CancellationToken tCancel )
		{
			Workspace tempWorkspace = _review.Workspace;
			ReviewPlan tempPlan = _review.Plan;
			Workspace.RequireSha( tempPlan.HeadSha );
			WorkerFailure.Require( tempPlan.IsReportRetry ? tempPlan.Steps.Length == 0 : tempPlan.Steps.Length > 0 && tempPlan.Steps.All( x => Regex.IsMatch( x.Name, "^[a-z][a-z0-9-]*$" ) && x.MaxAttempts > 0 ), "Invalid review steps", "infrastructure" );
			_review.WorkspaceHead = await tempWorkspace.GitAsync( [ "rev-parse", "HEAD" ], tCancel );
			if ( tempPlan.IsReportRetry || tempPlan.IsContinuation || tempPlan.ApprovedTreeSha is not null )
			{
				WorkerFailure.Require( ( await tempWorkspace.GitResultAsync( [ "merge-base", "--is-ancestor", _review.Head, "HEAD" ], tCancel ) ).IsSuccess, "Saved PR head is no longer in workspace history" );
			}
			else
			{
				WorkerFailure.Require( _review.WorkspaceHead == _review.Head && _review.Head == Storage.ReadText( Path.Combine( tempWorkspace.Job, "base-sha" ) ), "Checkout differs from the assigned PR head" );
			}
			if ( tempPlan.ApprovedTreeSha is not null )
			{
				Workspace.RequireSha( tempPlan.ApprovedTreeSha );
				WorkerFailure.Require( tempPlan.IsMergeRepair && _review.WorkspaceHead == Storage.ReadText( Path.Combine( tempWorkspace.Job, "base-sha" ) ) && await tempWorkspace.GitAsync( [ "rev-parse", "HEAD^{tree}" ], tCancel ) == tempPlan.ApprovedTreeSha && await tempWorkspace.FingerprintAsync( tCancel ) == tempPlan.ApprovedTreeSha, "Approved entry snapshot changed; preserving work" );
			}
			JsonElement tempPR = tempPlan.Context.GetProperty( "PullRequest" );
			WorkerFailure.Require( tempPR.GetProperty( "head" ).GetProperty( "sha" ).GetString() == _review.Head && tempPR.GetProperty( "state" ).GetString() == "open" && tempPR.GetProperty( "base" ).GetProperty( "ref" ).GetString() == tempWorkspace.Request.TargetBranch, "PR source or target changed before review" );
			if ( tempPlan.IsReportRetry )
			{
				string tempSource = _review.SourceRun( _review.PreviousRun );
				JsonObject tempManifest = Storage.ReadObject( Path.Combine( tempSource, "manifest.json" ) );
				string? tempReportSource = tempManifest[ "report_source_run" ]?.GetValue<string>();
				if ( tempReportSource is null && tempManifest[ "status" ]?.GetValue<string>() == "failed" && tempManifest[ "current_attempt" ] is null && Storage.Read<ReviewPlan>( Path.Combine( tempSource, "plan.json" ) ).IsReportRetry )
				{
					tempReportSource = _review.PreviousCheckpoint;
				}
				tempSource = _review.SourceRun( tempReportSource ?? tempSource );
				Storage.Update( _review.Manifest, x => { x[ "kind" ] = "report_retry"; x[ "report_source_run" ] = tempSource; } );
				tempManifest = Storage.ReadObject( Path.Combine( tempSource, "manifest.json" ) );
				_review.Target = tempManifest[ "target_sha" ]!.GetValue<string>();
				_review.Integration = tempManifest[ "integration_target_sha" ]?.GetValue<string>() ?? "";
				Workspace.RequireSha( _review.Target );
				await tempWorkspace.GitAsync( [ "cat-file", "-e", _review.Target + "^{commit}" ], tCancel );
				ReviewPlan tempSourcePlan = Storage.Read<ReviewPlan>( Path.Combine( tempSource, "plan.json" ) );
				WorkerFailure.Require( tempSourcePlan.Context.GetProperty( "PullRequest" ).GetProperty( "base" ).GetProperty( "ref" ).GetString() == tempWorkspace.Request.TargetBranch, "Saved report belongs to a different target branch" );
			}
			else
			{
				string? tempPinnedTarget = new ReviewContinuation( _review ).PinTarget();
				if ( tempPinnedTarget is not null )
				{
					_review.Target = tempPinnedTarget;
					await tempWorkspace.GitAsync( [ "cat-file", "-e", _review.Target + "^{commit}" ], tCancel );
				}
				else
				{
					await tempWorkspace.GitAsync( [ "fetch", "--no-tags", "origin", "refs/heads/" + tempWorkspace.Request.TargetBranch ], tCancel );
					_review.Target = await tempWorkspace.GitAsync( [ "rev-parse", "FETCH_HEAD" ], tCancel );
					WorkerFailure.Require( _review.Target == _review.ExpectedTarget, "Target changed while preparing review context" );
				}
				if ( _review.IsFeedbackRepair )
				{
					string tempBase = Storage.ReadText( Path.Combine( tempWorkspace.Job, "tree" ) );
					if ( tempBase.Length == 0 )
					{
						tempBase = tempPlan.PublishedSha is null ? tempPlan.PriorReview!.TreeSha : await tempWorkspace.GitAsync( [ "rev-parse", "HEAD^{tree}" ], tCancel );
					}
					Workspace.RequireSha( tempBase );
					_review.FeedbackPaths = ( await tempWorkspace.GitAsync( [ "diff", "--no-renames", "--name-only", "-z", tempBase, await tempWorkspace.FingerprintAsync( tCancel ) ], tCancel ) ).Split( '\0', StringSplitOptions.RemoveEmptyEntries );
				}
				string tempMerge = Storage.ReadText( Path.Combine( tempWorkspace.Root, ".git/MERGE_HEAD" ) );
				if ( tempMerge.Length > 0 )
				{
					WorkerFailure.Require( tempMerge == _review.Target, "Existing merge targets a different revision; preserving work" );
				}
				else
				{
					CommandResult tempMergeResult = await tempWorkspace.GitResultAsync( [ "merge", "--no-ff", "--no-commit", _review.Target ], tCancel );
					WorkerFailure.Require( tempMergeResult.IsSuccess || ( await tempWorkspace.GitAsync( [ "ls-files", "-u" ], tCancel ) ).Length > 0, "Merge failed without normal conflicts: " + tempMergeResult.Error, "integration" );
				}
				Storage.WriteText( Path.Combine( tempWorkspace.Job, "target-sha" ), _review.Target );
				_review.Integration = _review.Target;
			}
			if ( tempPlan.IsReportRetry || tempPlan.IsRereview || tempPlan.IsContinuation )
			{
				_review.Session = Storage.ReadText( Path.Combine( tempWorkspace.Job, "session-id" ) );
			}
			WorkerFailure.Require( _review.Session.Length == 0 || Regex.IsMatch( _review.Session, "^[A-Za-z0-9_-]{6,128}$" ), "Invalid saved Codex session" );
			_review.Comparison = await tempWorkspace.GitAsync( [ "merge-base", _review.Head, _review.Target ], tCancel );
			_review.OriginalPaths = ( await tempWorkspace.GitAsync( [ "diff", "--no-renames", "--name-only", "-z", _review.Comparison, _review.Head ], tCancel ) ).Split( '\0', StringSplitOptions.RemoveEmptyEntries );
			if ( _review.IsMergeRepair || _review.IsFeedbackRepair )
			{
				_review.ConflictPaths = ( await tempWorkspace.GitAsync( [ "diff", "--name-only", "--diff-filter=U", "-z" ], tCancel ) ).Split( '\0', StringSplitOptions.RemoveEmptyEntries );
				_review.ScopeBase = await tempWorkspace.FingerprintAsync( tCancel );
			}
			WorkerFailure.Require( !_review.IsFeedbackRepair || ( _review.Feedback.Length > 0 && _review.Feedback.All( x => !string.IsNullOrEmpty( x.Key ) && !x.Path.StartsWith( '/' ) && !x.Path.Split( '/' ).Contains( ".." ) ) ), "Invalid CodeRabbit batch", "infrastructure" );
			Storage.Write( Path.Combine( _review.Run, "conflict-paths.json" ), _review.ConflictPaths );
			Storage.Write( Path.Combine( _review.Run, "feedback-existing-paths.json" ), _review.FeedbackPaths );
			Storage.Write( Path.Combine( _review.Run, "original-pr-paths.json" ), _review.OriginalPaths );
			CommandResult tempVersion = await Commands.RunAsync( "codex", [ "--version" ], tCancel: tCancel );
			Storage.Update( _review.Manifest, x =>
			{
				x[ "codex_version" ] = tempVersion.Output.Trim();
				x[ "head_sha" ] = _review.Head;
				x[ "target_sha" ] = _review.Target;
				x[ "comparison_base_sha" ] = _review.Comparison;
				x[ "integration_target_sha" ] = _review.Integration;
				if ( !tempPlan.IsReportRetry && ( _review.IsMergeRepair || _review.IsFeedbackRepair ) )
				{
					x[ "kind" ] = _review.IsFeedbackRepair ? "feedback_repair" : "merge_repair";
					x[ "scope_base_tree" ] = _review.ScopeBase;
					x[ "merge_session_id" ] = _review.Run;
					if ( _review.IsFeedbackRepair )
					{
						x[ "feedback_session_id" ] = _review.Run;
						x[ "feedback" ] = JsonSerializer.SerializeToNode( _review.Feedback.Select( y => new { key = y.Key, version = y.Version, url = y.Url } ) );
					}
				}
			} );
		}

		private async Task RunStepAsync( int tIndex, CancellationToken tCancel )
		{
			ReviewStep tempStep = _review.Plan.Steps[ tIndex ];
			_review.IsFinal = tIndex == _review.Plan.Steps.Length - 1;
			string tempPriorStage = _review.Attempt;

			int tempNumber = 0;
			int tempConsumed = 0;
			string tempPrevious = "";
			foreach ( string tempSaved in Directory.GetDirectories( _review.Run, $"{tIndex}-{tempStep.Name}-*" ).Order( StringComparer.Ordinal ) )
			{
				JsonObject tempAttempt = Storage.ReadObject( Path.Combine( tempSaved, "attempt.json" ) );
				int tempSavedNumber = int.Parse( Path.GetFileName( tempSaved ).Split( '-' ).Last(), CultureInfo.InvariantCulture );
				if ( tempSavedNumber > tempNumber )
				{
					tempNumber = tempSavedNumber;
					tempPrevious = tempSaved;
				}
				if ( ( tempAttempt[ "operation_id" ]?.GetValue<string>() ?? _review.Plan.OperationId ) == _review.Plan.OperationId && tempAttempt[ "exit_code" ]?.GetValue<int>() == 0 && tempAttempt[ "is_complete" ]?.GetValue<bool>() != true )
				{
					++tempConsumed;
				}
			}
			WorkerFailure? tempFailure = null;
			while ( tempConsumed < tempStep.MaxAttempts )
			{
				++tempNumber;
				_review.Attempt = Path.Combine( _review.Run, $"{tIndex}-{tempStep.Name}-{tempNumber}" );
				Directory.CreateDirectory( _review.Attempt );
				string tempInputTree = await _review.Workspace.FingerprintAsync( tCancel );
				ReviewScope tempScope = await _review.ScopeAsync( tempInputTree, tCancel );
				Storage.Write( Path.Combine( _review.Attempt, "input-scope.json" ), tempScope, true );
				Storage.Write( Path.Combine( _review.Artifacts, "review-scope.json" ), tempScope, true );
				Storage.Write( Path.Combine( _review.Attempt, "attempt.json" ), new { step = tempStep.Name, attempt = tempNumber, operation_id = _review.Plan.OperationId, model = tempStep.Model, reasoning_effort = tempStep.ReasoningEffort, started_at = DateTimeOffset.UtcNow, input_tree = tempInputTree, status = "running" } );
				Storage.Update( _review.Manifest, x => { x[ "attempts" ]!.AsArray().Add( _review.Attempt ); x[ "current_attempt" ] = _review.Attempt; } );
				int tempCode = 143;
				try
				{
					tempCode = await _review.Model.RunAsync( tempStep, _review.Workspace.Root, _review.Attempt, ReviewPrompt.Render( _review, tempStep, tempPrevious, tempPriorStage ), ModelRunner.Schema( _review.Run, "result.schema.json" ), true, _review.Artifacts, _review.Session, tCancel );
				}
				finally
				{
					Storage.Update( Path.Combine( _review.Attempt, "attempt.json" ), x =>
					{
						x[ "completed_at" ] = DateTimeOffset.UtcNow;
						x[ "exit_code" ] = tempCode;
						x[ "status" ] = tempCode == 0 ? "executed" : "failed";
						x[ "usage" ] = JsonSerializer.SerializeToNode( Usage.Read( Path.Combine( _review.Attempt, "events.jsonl" ), tempCode ), Storage.ReportOptions );
					} );
				}
				ReadSession();
				RequireModelSuccess( tempCode, Path.Combine( _review.Attempt, "codex.stderr" ) );
				++tempConsumed;
				_review.OutputTree = await _review.Workspace.FingerprintAsync( tCancel );
				Storage.Write( Path.Combine( _review.Attempt, "output-scope.json" ), await _review.ScopeAsync( _review.OutputTree, tCancel ), true );
				Storage.Update( Path.Combine( _review.Attempt, "attempt.json" ), x => { x[ "session_id" ] = _review.Session; x[ "output_tree" ] = _review.OutputTree; } );
				_review.IsCommitRequired = _review.OutputTree != await _review.Workspace.GitAsync( [ "rev-parse", "HEAD^{tree}" ], tCancel ) || File.Exists( Path.Combine( _review.Workspace.Root, ".git/MERGE_HEAD" ) );
				bool tempIsComplete = await new ReportCompletion( _review ).CompleteAsync( tCancel );
				ReviewResult tempResult = Storage.Read<ReviewResult>( Path.Combine( _review.Attempt, "result.json" ), true );
				string tempReport = ReportWriter.Render( tempResult );
				Storage.WriteText( Path.Combine( _review.Attempt, "report.md" ), tempReport );
				Storage.WriteText( Path.Combine( _review.Workspace.Job, "report.md" ), tempReport );
				Storage.Update( Path.Combine( _review.Attempt, "attempt.json" ), x => x[ "is_complete" ] = tempIsComplete );
				if ( tempIsComplete )
				{
					return;
				}
				JsonObject tempMetadata = Storage.ReadObject( Path.Combine( _review.Attempt, "attempt.json" ) );
				string? tempDiagnosis = tempMetadata[ "report_recovery" ]?[ "diagnosis" ]?[ "category" ]?.GetValue<string>();
				bool tempHasRelevanceEvidence = !string.IsNullOrWhiteSpace( tempResult.Relevance.Details ) && !string.IsNullOrWhiteSpace( tempResult.Relevance.Evidence );
				string tempCategory = tempResult.Relevance.Status == "not_needed" ? ( tempHasRelevanceEvidence ? "not_needed" : "unfinished_review" ) : tempResult.Relevance.Status == "uncertain" ? "maintainer_decision" : tempResult.FailureKind != "none" ? tempResult.FailureKind : tempResult.Outcome == "blocked" ? "maintainer_decision" : tempDiagnosis ?? ( tempResult.Checks.Any( x => x.IsRequired && x.Status != "passed" && !( x.IsBaselineFailure && x.Status == "failed" ) ) ? "required_check" : tempResult.Outcome == "needs_followup" || tempResult.Coverage.Values.Any( x => x.Status == "incomplete" ) ? "unfinished_review" : "report_metadata" );
				string tempAction = tempResult.FollowUp + " " + tempResult.Summary + " " + ( tempMetadata[ "report_recovery" ]?[ "diagnosis" ]?[ "next_action" ]?.GetValue<string>() ?? Storage.ReadText( Path.Combine( _review.Attempt, "gate.txt" ) ) );
				tempFailure = new( tempCategory, tempAction[ ..Math.Min( tempAction.Length, 2000 ) ] );
				Storage.Write( Path.Combine( _review.Attempt, "failure.json" ), new { category = tempFailure.Category, action = tempFailure.Message } );
				if ( tempCategory is not ( "missing_evidence" or "unfinished_review" or "required_check" or "integration" ) )
				{
					throw tempFailure;
				}
				tempPrevious = _review.Attempt;
			}

			string tempRemainingWork = tempFailure?.Message ?? "";
			if ( tempRemainingWork.Length == 0 && tempPrevious.Length > 0 && File.Exists( Path.Combine( tempPrevious, "result.json" ) ) )
			{
				tempRemainingWork = Storage.Read<ReviewResult>( Path.Combine( tempPrevious, "result.json" ), true ).FollowUp;
			}

			throw new WorkerFailure( "attempt_limit", $"Review stage '{tempStep.Name}' used its {tempStep.MaxAttempts} attempts. Saved work is retained; use cpr continue {_review.Workspace.Request.Id} to continue unfinished work with a fresh bounded allowance, or connect for manual repairs. " + tempRemainingWork[ ..Math.Min( tempRemainingWork.Length, 1500 ) ] );
		}

		private void ReadSession()
		{
			foreach ( string tempLine in File.ReadLines( Path.Combine( _review.Attempt, "events.jsonl" ) ) )
			{
				try
				{
					JsonObject? tempEvent = JsonNode.Parse( tempLine ) as JsonObject;
					string? tempSession = tempEvent?[ "type" ]?.GetValue<string>() == "thread.started" ? tempEvent[ "thread_id" ]?.GetValue<string>() : null;
					if ( tempSession is not null && Regex.IsMatch( tempSession, "^[A-Za-z0-9_-]{6,128}$" ) )
					{
						_review.Session = tempSession;
						Storage.WriteText( Path.Combine( _review.Workspace.Job, "session-id" ), tempSession );
					}
				}
				catch ( JsonException )
				{
					// Partial event lines remain available in the raw event log
				}
			}
		}

		public static void RequireModelSuccess( int tCode, string tErrorPath )
		{
			if ( tCode != 0 )
			{
				string tempError = Storage.ReadText( tErrorPath );
				string tempCategory = tCode is 124 or 137 or 143 or 130 ? "interrupted" : WorkerFailure.IsTransient( tempError ) ? "transient" : "infrastructure";

				throw new WorkerFailure( tempCategory, $"Model execution exited {tCode}; see {tErrorPath}. {tempError[ ..Math.Min( tempError.Length, 1000 ) ]}", tCode );
			}
		}
	}
}
