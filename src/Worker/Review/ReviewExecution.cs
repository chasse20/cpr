using CPR.Worker.Execution;
using CPR.Worker.Git;
using CPR.Worker.Persistence;
using CPR.Worker.Reports;

namespace CPR.Worker.Review
{
	public sealed class ReviewExecution
	{
		public Workspace Workspace { get; }
		public ReviewPlan Plan { get; }
		public ModelRunner Model { get; }
		public string Run { get; }
		public string Artifacts { get; }
		public string Manifest => Path.Combine( Run, "manifest.json" );
		public string PreviousRun { get; }
		public string PreviousCheckpoint { get; }
		public string Attempt { get; set; } = "";
		public string Session { get; set; } = "";
		public string Head => Plan.HeadSha;
		public string Target { get; set; } = "";
		public string ExpectedTarget => Plan.Context.GetProperty( "TargetSha" ).GetString()!;
		public string Comparison { get; set; } = "";
		public string Integration { get; set; } = "";
		public string WorkspaceHead { get; set; } = "";
		public string? WorkspaceTree { get; set; }
		public string OutputTree { get; set; } = "";
		public string? ScopeBase { get; set; }
		public string[] OriginalPaths { get; set; } = [];
		public string[] ConflictPaths { get; set; } = [];
		public string[] FeedbackPaths { get; set; } = [];
		public FeedbackComment[] Feedback { get; set; }
		public bool IsMergeRepair { get; set; }
		public bool IsFeedbackRepair { get; set; }
		public bool IsHistorical { get; set; }
		public bool IsFinal { get; set; }
		public bool IsCommitRequired { get; set; }
		public string CommentScope => IsMergeRepair || IsFeedbackRepair || Plan.PublishedSha is not null ? "commit_summary" : "full_review";

		public ReviewExecution( Workspace tWorkspace, ReviewPlan tPlan )
		{
			Workspace = tWorkspace;
			Plan = tPlan;
			ProcessControl.RequireOperation( tPlan.OperationId );
			Model = new( tWorkspace, tPlan.OperationId );
			PreviousRun = Storage.ReadText( Path.Combine( tWorkspace.Job, "latest-run" ) );
			PreviousCheckpoint = Storage.ReadText( Path.Combine( tWorkspace.Job, "review-checkpoint" ) );
			Run = Storage.CreateRun( tWorkspace.Job );
			Artifacts = Path.Combine( Run, "artifacts" );
			Directory.CreateDirectory( Path.Combine( Artifacts, "evidence" ) );
			IsMergeRepair = tPlan.IsMergeRepair;
			IsFeedbackRepair = tPlan.IsFeedbackRepair;
			Feedback = tPlan.Feedback;
			Storage.Write( Path.Combine( Run, "plan.json" ), tPlan );
			Storage.Write( Path.Combine( Artifacts, "prior-review.json" ), tPlan.PriorReview! );
			Storage.Write( Path.Combine( Artifacts, "feedback.json" ), tPlan.Feedback );
			Storage.Write( Path.Combine( Artifacts, "context.json" ), tPlan.Context );
			Storage.Write( Manifest, new { cpr_id = tWorkspace.Request.Id, url = tWorkspace.Url, status = "running", started_at = DateTimeOffset.UtcNow, scratch = Artifacts, maintainer_resolution = tPlan.MaintainerResolution, previous_published_sha = tPlan.PublishedSha, report_kind = tPlan.PublishedSha is null ? "full_review" : "commit_summary", attempts = Array.Empty<string>() } );
			Storage.WriteText( Path.Combine( tWorkspace.Job, "operation-" + tPlan.OperationId ), Run );
			Storage.WriteText( Path.Combine( tWorkspace.Job, "latest-run" ), Run );
		}

		public string SourceRun( string tPath )
		{
			Storage.RequireCanonical( tPath );
			WorkerFailure.Require( Path.GetDirectoryName( tPath ) == Path.Combine( Workspace.Job, "runs" ) && Regex.IsMatch( Path.GetFileName( tPath ), "^[A-Za-z0-9_-]+$" ) && Directory.Exists( tPath ), "Invalid saved review run; preserving work" );

			return tPath;
		}

		public async Task<ReviewScope> ScopeAsync( string tTree, CancellationToken tCancel )
		{
			string[] tempChanged = ( await Workspace.GitAsync( [ "diff", "--no-renames", "--name-only", "-z", ScopeBase ?? Head, tTree ], tCancel ) ).Split( '\0', StringSplitOptions.RemoveEmptyEntries );
			IEnumerable<string> tempRequired = IsFeedbackRepair ? ConflictPaths.Concat( FeedbackPaths ).Concat( Feedback.Where( x => x.Path.Length > 0 ).Select( x => x.Path ) ) : IsMergeRepair ? ConflictPaths : OriginalPaths;

			return new() { ComparisonBaseSha = Comparison, HeadSha = Head, TargetSha = Target, OriginalPrPaths = OriginalPaths, WorkingTreePaths = tempChanged, ScopeKind = IsFeedbackRepair ? "feedback_repair" : IsMergeRepair ? "merge_repair" : "full_review", ConflictPaths = ConflictPaths, RequiredPaths = [ .. tempRequired.Concat( tempChanged ).Distinct( StringComparer.Ordinal ).Order( StringComparer.Ordinal ) ] };
		}

		public async Task RequireUnchangedAsync( CancellationToken tCancel )
		{
			Model.RequireNotCancelled();
			WorkerFailure.Require( await Workspace.GitAsync( [ "rev-parse", "HEAD" ], tCancel ) == WorkspaceHead && await Workspace.FingerprintAsync( tCancel ) == ( WorkspaceTree ?? OutputTree ), "Workspace changed after validation; preserving saved work" );
		}

		public async Task<bool> CheckReportAsync( CancellationToken tCancel, ReviewResult? tCandidate = null, string? tGate = null )
		{
			List<string> tempErrors = [];
			try
			{
				ReviewResult tempResult = tCandidate ?? Storage.Read<ReviewResult>( Path.Combine( Attempt, "result.json" ), true );
				tempErrors.AddRange( ReportValidator.Validate( tempResult, IsFinal, IsCommitRequired, Plan.ApprovedTreeSha is not null ) );
				foreach ( string tempArtifact in tempResult.Checks.Select( x => x.Artifact ).Append( tempResult.ScopeAudit ).Where( x => x.Length > 0 ) )
				{
					Storage.Evidence( Artifacts, tempArtifact );
				}
				bool tempIsHandoff = ReportValidator.IsHandoff( tempResult, IsFinal );

				if ( IsFinal || tempIsHandoff )
				{
					ScopeAudit tempAudit = Storage.Read<ScopeAudit>( Storage.Evidence( Artifacts, tempResult.ScopeAudit ), true );
					ReviewScope tempScope = Storage.Read<ReviewScope>( Path.Combine( Attempt, "output-scope.json" ), true );
					ReportValidator.ValidateScope( tempAudit, tempScope, tempResult, tempIsHandoff );
					CommandResult tempDiff = await Workspace.GitResultAsync( [ "diff", "--check", Head, OutputTree ], tCancel );
					WorkerFailure.Require( tempDiff.IsSuccess, "Resolve conflict markers and whitespace errors: " + tempDiff.Output, "integration" );
				}
				if ( IsFeedbackRepair )
				{
					FeedbackDisposition[] tempFeedback = Storage.Read<FeedbackDisposition[]>( Storage.Evidence( Artifacts, "evidence/feedback-dispositions.json" ), true );
					ReportValidator.ValidateFeedback( tempFeedback, Feedback, tempResult );
				}
			}
			catch ( Exception tException ) when ( tException is JsonException or IOException or WorkerFailure )
			{
				tempErrors.Add( tException.Message );
			}
			Storage.WriteText( tGate ?? Path.Combine( Attempt, "gate.txt" ), string.Join( '\n', tempErrors ) );

			return tempErrors.Count == 0;
		}

		public async Task<int> RunAuxiliaryAsync( string tName, ReviewStep tStep, string tPrompt, string tSchema, CancellationToken tCancel )
		{
			string tempDirectory = Path.Combine( Attempt, tName );
			string tempKey = tName.StartsWith( "report-repair-", StringComparison.Ordinal ) ? "report_repairs" : tName.Replace( '-', '_' );
			JsonObject tempMetadata = new() { [ "model" ] = tStep.Model, [ "reasoning_effort" ] = tStep.ReasoningEffort, [ "started_at" ] = DateTimeOffset.UtcNow, [ "status" ] = "running" };
			Storage.Update( Path.Combine( Attempt, "attempt.json" ), x =>
			{
				if ( tempKey == "report_repairs" )
				{
					x[ tempKey ] ??= new JsonArray();
					x[ tempKey ]!.AsArray().Add( tempMetadata.DeepClone() );
				}
				else
				{
					x[ tempKey ] = tempMetadata.DeepClone();
				}
			} );
			int tempCode = 143;
			try
			{
				tempCode = await Model.RunAsync( tStep, tempDirectory, tempDirectory, tPrompt, ModelRunner.Schema( Run, tSchema ), false, null, null, tCancel );
				await RequireUnchangedAsync( tCancel );

				return tempCode;
			}
			finally
			{
				Storage.Update( Path.Combine( Attempt, "attempt.json" ), x =>
				{
					JsonNode tempRecord = tempKey == "report_repairs" ? x[ tempKey ]!.AsArray().Last()! : x[ tempKey ]!;
					tempRecord[ "completed_at" ] = DateTimeOffset.UtcNow;
					tempRecord[ "exit_code" ] = tempCode;
					tempRecord[ "status" ] = tempCode == 0 ? "executed" : "failed";
					tempRecord[ "usage" ] = JsonSerializer.SerializeToNode( Usage.Read( Path.Combine( tempDirectory, "events.jsonl" ), tempCode ), Storage.ReportOptions );
				} );
			}
		}
	}
}
