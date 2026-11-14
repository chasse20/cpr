using CPR.Service.SSH;
using CPR.SQL;

namespace CPR.Service.Review
{
	public static class ReviewComment
	{
		public static async Task<long> PrepareAsync( Context tContext, PullRequest tRequest, WorkerSnapshot tSnapshot, CancellationToken tCancel )
		{
			ReviewRun tempRun = await tContext.ReviewRun.SingleAsync( x => x.PullRequestId == tRequest.PullRequestId && x.WorkerId == tRequest.WorkerId && x.RunKey == tSnapshot.RunKey, tCancel );
			string tempBody = ReadBody( tempRun, tRequest, tSnapshot.TreeSha, tSnapshot.TargetSha );

			if ( tempRun.PublishedSha is not null || ( tempRun.CommentBody is not null && tempRun.CommentBody != tempBody ) )
			{
				throw new InvalidOperationException( "Review comment changed or was already published; request a fresh review" );
			}

			tempRun.CommentBody = tempBody;

			return tempRun.ReviewRunId;
		}

		public static async Task<ReviewRun> GetPreparedAsync( Context tContext, PullRequest tRequest, CancellationToken tCancel )
		{
			ReviewRun? tempRun = await tContext.ReviewRun.SingleOrDefaultAsync( x => x.ReviewRunId == tRequest.PreparedReviewRunId && x.PullRequestId == tRequest.PullRequestId && x.WorkerId == tRequest.WorkspaceWorkerId, tCancel );

			if ( tempRun is null || tempRun.CommentBody is null || tempRun.PublishedSha is not null || tRequest.PreparedSha is null || tRequest.ReviewedTargetSha is null || tempRun.CommentBody != ReadBody( tempRun, tRequest, tRequest.PreparedSha, tRequest.ReviewedTargetSha ) )
			{
				throw new InvalidOperationException( "A PR comment for this exact snapshot is required; run cpr review before approving again" );
			}

			return tempRun;
		}

		private static string ReadBody( ReviewRun tRun, PullRequest tRequest, string tTreeSha, string tTargetSha )
		{
			using JsonDocument tempDocument = JsonDocument.Parse( tRun.Details );
			JsonElement tempManifest = tempDocument.RootElement.GetProperty( "manifest" );
			JsonElement tempResult = tempDocument.RootElement.GetProperty( "result" );

			if ( tempManifest.GetProperty( "status" ).GetString() != "complete" || tempManifest.GetProperty( "reviewed_tree" ).GetString() != tTreeSha || tempManifest.GetProperty( "target_sha" ).GetString() != tTargetSha || tempManifest.GetProperty( "head_sha" ).GetString() != tRequest.RemoteSha || tempResult.GetProperty( "outcome" ).GetString() != "complete" )
			{
				throw new InvalidOperationException( "PR comment does not belong to the completed review of this snapshot" );
			}

			string? tempBody = tempResult.TryGetProperty( "pr_comment", out JsonElement tempComment ) && tempComment.ValueKind == JsonValueKind.String ? tempComment.GetString() : null;

			if ( string.IsNullOrWhiteSpace( tempBody ) || tempBody.EnumerateRunes().Count() > 12000 || tempBody.Any( x => char.IsControl( x ) && x is not ( '\n' or '\t' ) ) || tempBody.Contains( "<!-- cpr-report:", StringComparison.Ordinal ) )
			{
				throw new InvalidOperationException( "Review must supply a readable PR comment of at most 12000 characters without control characters or CPR delivery markers" );
			}

			return tempBody;
		}
	}
}
