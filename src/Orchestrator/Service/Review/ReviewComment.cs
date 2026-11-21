using CPR.SQL;
using System.Text.Json.Nodes;

namespace CPR.Service.Review
{
	public static class ReviewComment
	{
		public static async Task<ReviewReport> GetFullReportAsync( Context tContext, PullRequest tRequest, CancellationToken tCancel )
		{
			return await FindFullReportAsync( tContext, tRequest, tCancel ) ?? throw new InvalidOperationException( "No completed full review report is available; recover the saved report before completing tweaks" );
		}

		public static async Task<ReviewReport?> FindFullReportAsync( Context tContext, PullRequest tRequest, CancellationToken tCancel )
		{
			List<ReviewRun> tempRuns = await tContext.ReviewRun.AsNoTracking().Where( x => x.PullRequestId == tRequest.PullRequestId ).OrderByDescending( x => x.ReviewRunId ).ToListAsync( tCancel );

			return FindReport( tempRuns, true );
		}

		public static async Task<long> PrepareAsync( Context tContext, PullRequest tRequest, WorkerSnapshot tSnapshot, CancellationToken tCancel )
		{
			ReviewRun tempRun = await tContext.ReviewRun.SingleAsync( x => x.PullRequestId == tRequest.PullRequestId && x.WorkerId == tRequest.WorkerId && x.RunKey == tSnapshot.RunKey, tCancel );
			List<ReviewRun> tempRuns = await tContext.ReviewRun.AsNoTracking().Where( x => x.PullRequestId == tRequest.PullRequestId && x.ReviewRunId <= tempRun.ReviewRunId ).OrderBy( x => x.ReviewRunId ).ToListAsync( tCancel );
			string tempBody = ComposeBody( tRequest, tempRun, tempRuns, tSnapshot.TreeSha, tSnapshot.TargetSha );

			if ( tempRun.PublishedSha is not null || ( tempRun.CommentBody is not null && tempRun.CommentBody != tempBody ) )
			{
				throw new InvalidOperationException( "Review comment changed or was already published; complete a fresh review or tweak session" );
			}

			// Retain the composed preview with the exact snapshot; history capture cannot replace it
			JsonObject tempDetails = JsonNode.Parse( tempRun.Details )!.AsObject();
			tempDetails[ "publication" ] = new JsonObject
			{
				[ "head_sha" ] = tRequest.RemoteSha,
				[ "tree_sha" ] = tSnapshot.TreeSha,
				[ "target_sha" ] = tSnapshot.TargetSha,
				[ "previous_published_sha" ] = tRequest.PublishedSha,
				[ "body" ] = tempBody
			};
			tempRun.Details = tempDetails.ToJsonString();
			tempRun.CommentBody = tempBody;

			return tempRun.ReviewRunId;
		}

		public static async Task<ReviewRun> GetPreparedAsync( Context tContext, PullRequest tRequest, CancellationToken tCancel )
		{
			// Review provenance stays on the original worker when its approved checkout moves
			ReviewRun? tempRun = await tContext.ReviewRun.SingleOrDefaultAsync( x => x.ReviewRunId == tRequest.PreparedReviewRunId && x.PullRequestId == tRequest.PullRequestId, tCancel );

			if ( tempRun is null || tempRun.CommentBody is null || tempRun.PublishedSha is not null || tRequest.PreparedSha is null || tRequest.ReviewedTargetSha is null || tempRun.CommentBody != ReadPreparedBody( tempRun, tRequest ) )
			{
				throw new InvalidOperationException( "A PR comment for this exact snapshot is required; complete a fresh review or tweak session before approving again" );
			}

			return tempRun;
		}

		private static string ReadPreparedBody( ReviewRun tRun, PullRequest tRequest )
		{
			string tempBody = ReadBody( tRun, tRequest.RemoteSha, tRequest.PreparedSha!, tRequest.ReviewedTargetSha! );
			using JsonDocument tempDocument = JsonDocument.Parse( tRun.Details );
			JsonElement tempRoot = tempDocument.RootElement;

			if ( tempRoot.TryGetProperty( "publication", out JsonElement tempPublication ) )
			{
				if ( tempPublication.GetProperty( "head_sha" ).GetString() != tRequest.RemoteSha || tempPublication.GetProperty( "tree_sha" ).GetString() != tRequest.PreparedSha || tempPublication.GetProperty( "target_sha" ).GetString() != tRequest.ReviewedTargetSha || tempPublication.GetProperty( "previous_published_sha" ).GetString() != tRequest.PublishedSha )
				{
					throw new InvalidOperationException( "Publication preview belongs to a different snapshot or publication" );
				}

				tempBody = tempPublication.GetProperty( "body" ).GetString()!;
				ValidateBody( tempBody, 60000 );
			}
			else if ( IsSupplement( tempRoot.GetProperty( "manifest" ) ) )
			{
				throw new InvalidOperationException( "Complete a new cpr connect session to prepare the full report and session summaries" );
			}

			return tempBody;
		}

		private static string ComposeBody( PullRequest tRequest, ReviewRun tRun, List<ReviewRun> tRuns, string tTreeSha, string tTargetSha )
		{
			string tempBody = ReadBody( tRun, tRequest.RemoteSha, tTreeSha, tTargetSha );
			using JsonDocument tempDocument = JsonDocument.Parse( tRun.Details );
			JsonElement tempManifest = tempDocument.RootElement.GetProperty( "manifest" );
			string tempKind = tRequest.PublishedSha is null ? "full_review" : "commit_summary";

			if ( tempManifest.GetProperty( "report_kind" ).GetString() != tempKind || tempManifest.GetProperty( "previous_published_sha" ).GetString() != tRequest.PublishedSha )
			{
				throw new InvalidOperationException( "Report was prepared for a different publication; complete a fresh review or tweak session" );
			}

			if ( IsSupplement( tempManifest ) )
			{
				long tempBoundary = tRuns.Where( x => IsPublished( x ) ).Select( x => x.ReviewRunId ).DefaultIfEmpty().Max();
				List<ReviewRun> tempPending = [ .. tRuns.Where( x => x.ReviewRunId > tempBoundary ).OrderBy( x => x.ReviewRunId ) ];
				ReviewReport? tempReport = FindReport( tempPending.AsEnumerable().Reverse(), tRequest.PublishedSha is null );

				if ( tRequest.PublishedSha is null && tempReport is null )
				{
					throw new InvalidOperationException( "The first publication requires a completed full CR report; run cpr review" );
				}

				List<string> tempParts = [];

				if ( tempReport is not null )
				{
					tempParts.Add( $"{tempReport.Body}\n\n---\n\nThe report above applies to the saved review tree `{tempReport.TreeSha}`. Subsequent focused repairs and manual changes are recorded below; the full review was not rerun for those changes." );
					tempBoundary = tempReport.ReviewRunId;
				}

				// A retried completion replaces only its own session summary
				Dictionary<string, (long Id, string Body, string Title)> tempSessions = [];

				foreach ( ReviewRun tempPendingRun in tempPending.Where( x => x.ReviewRunId > tempBoundary ) )
				{
					using JsonDocument tempPendingDocument = JsonDocument.Parse( tempPendingRun.Details );
					JsonElement tempPendingManifest = tempPendingDocument.RootElement.GetProperty( "manifest" );

					if ( IsComplete( tempPendingManifest ) && IsSupplement( tempPendingManifest ) && tempPendingManifest.GetProperty( "previous_published_sha" ).GetString() == tRequest.PublishedSha )
					{
						string tempSessionKind = IsManual( tempPendingManifest ) ? "tweak" : IsMergeRepair( tempPendingManifest ) ? "merge" : "feedback";
						string tempSessionId = tempPendingManifest.TryGetProperty( $"{tempSessionKind}_session_id", out JsonElement tempSession ) ? tempSession.GetString()! : $"{tempPendingRun.WorkerId}/{tempPendingRun.RunKey}";
						string tempTitle = tempSessionKind == "tweak" ? "Manual session" : tempSessionKind == "merge" ? "Target integration" : "CodeRabbit repair";
						string tempSessionBody = ReadBody( tempPendingRun, tempPendingManifest.GetProperty( "head_sha" ).GetString()!, tempPendingManifest.GetProperty( "reviewed_tree" ).GetString()!, tempPendingManifest.GetProperty( "target_sha" ).GetString()! );
						tempSessions[ tempSessionId ] = (tempPendingRun.ReviewRunId, tempSessionBody, tempTitle);
					}
				}

				int tempIndex = 0;

				foreach ( (long _, string tempSessionBody, string tempTitle) in tempSessions.Values.OrderBy( x => x.Id ) )
				{
					tempParts.Add( $"## {tempTitle} {++tempIndex}\n\n{tempSessionBody}" );
				}

				tempParts.Add( "CPR checked the final Git snapshot and target integration. Each session reports its own checks and scope; the original full review was not repeated." );
				tempBody = string.Join( "\n\n", tempParts );
				ValidateBody( tempBody, 60000 );
			}

			return tempBody;
		}

		private static ReviewReport? FindReport( IEnumerable<ReviewRun> tRuns, bool tIsFullRequired )
		{
			foreach ( ReviewRun tempRun in tRuns )
			{
				using JsonDocument tempDocument = JsonDocument.Parse( tempRun.Details );
				JsonElement tempRoot = tempDocument.RootElement;

				if ( tempRoot.TryGetProperty( "manifest", out JsonElement tempManifest ) && IsComplete( tempManifest ) && !IsSupplement( tempManifest ) && ( !tIsFullRequired || !tempManifest.TryGetProperty( "report_kind", out JsonElement tempKind ) || tempKind.GetString() == "full_review" ) && tempRoot.TryGetProperty( "result", out JsonElement tempResult ) && tempResult.ValueKind == JsonValueKind.Object && tempResult.TryGetProperty( "pr_comment", out JsonElement tempComment ) && tempComment.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace( tempComment.GetString() ) )
				{
					string tempHead = tempManifest.GetProperty( "head_sha" ).GetString()!;
					string tempTarget = tempManifest.GetProperty( "target_sha" ).GetString()!;
					string tempTree = tempManifest.GetProperty( "reviewed_tree" ).GetString()!;

					return new( tempRun.ReviewRunId, tempRun.RunKey, tempHead, tempTarget, tempTree, ReadBody( tempRun, tempHead, tempTree, tempTarget ) );
				}
			}

			return null;
		}

		private static bool IsPublished( ReviewRun tRun )
		{
			using JsonDocument tempDocument = JsonDocument.Parse( tRun.Details );

			return tRun.PublishedSha is not null || ( tempDocument.RootElement.TryGetProperty( "publication", out JsonElement tempPublication ) && tempPublication.TryGetProperty( "published_sha", out JsonElement tempSha ) && tempSha.ValueKind == JsonValueKind.String );
		}

		private static bool IsComplete( JsonElement tManifest )
		{
			return tManifest.ValueKind == JsonValueKind.Object && tManifest.TryGetProperty( "status", out JsonElement tempStatus ) && tempStatus.GetString() == "complete";
		}

		private static bool IsSupplement( JsonElement tManifest )
		{
			return IsManual( tManifest ) || IsMergeRepair( tManifest ) || ( tManifest.TryGetProperty( "kind", out JsonElement tempKind ) && tempKind.GetString() == "feedback_repair" ) || ( tManifest.TryGetProperty( "review_scope", out JsonElement tempScope ) && tempScope.GetString() == "feedback_repair" );
		}

		private static bool IsMergeRepair( JsonElement tManifest )
		{
			return ( tManifest.TryGetProperty( "kind", out JsonElement tempKind ) && tempKind.GetString() == "merge_repair" ) || ( tManifest.TryGetProperty( "review_scope", out JsonElement tempScope ) && tempScope.GetString() == "merge_repair" );
		}

		private static bool IsManual( JsonElement tManifest )
		{
			return tManifest.TryGetProperty( "kind", out JsonElement tempKind ) && tempKind.GetString() == "manual_tweak";
		}

		private static string ReadBody( ReviewRun tRun, string tHeadSha, string tTreeSha, string tTargetSha )
		{
			using JsonDocument tempDocument = JsonDocument.Parse( tRun.Details );
			JsonElement tempManifest = tempDocument.RootElement.GetProperty( "manifest" );
			JsonElement tempResult = tempDocument.RootElement.GetProperty( "result" );

			if ( !IsComplete( tempManifest ) || tempManifest.GetProperty( "reviewed_tree" ).GetString() != tTreeSha || tempManifest.GetProperty( "target_sha" ).GetString() != tTargetSha || tempManifest.GetProperty( "head_sha" ).GetString() != tHeadSha || tempResult.GetProperty( "outcome" ).GetString() != "complete" )
			{
				throw new InvalidOperationException( "PR comment does not belong to the completed review of this snapshot" );
			}

			string? tempBody = tempResult.TryGetProperty( "pr_comment", out JsonElement tempComment ) && tempComment.ValueKind == JsonValueKind.String ? tempComment.GetString() : null;
			ValidateBody( tempBody, 12000 );

			return tempBody!;
		}

		public static void ValidateBody( string? tBody, int tLimit )
		{
			if ( string.IsNullOrWhiteSpace( tBody ) || tBody.EnumerateRunes().Count() > tLimit || tBody.Any( x => char.IsControl( x ) && x is not ( '\n' or '\t' ) ) || tBody.Contains( "<!-- cpr-report:", StringComparison.Ordinal ) )
			{
				throw new InvalidOperationException( $"PR comment must be readable, at most {tLimit} characters, and contain no control characters or CPR delivery markers; a fresh full review can consolidate pending session reports" );
			}
		}
	}
}
