using CPR.Service;
using CPR.Service.GitHub;
using CPR.Service.Queue;
using CPR.Service.Review;
using CPR.SQL;

namespace CPR.Controller
{
	[ApiController]
	[Route( "api/pr" )]
	public sealed class PublicationController( Context tContext, QueueGate tGate, GitHubClient tGitHub, IOptions<Settings> tSettings ) : ControllerBase
	{
		public sealed record PublicationRequest( [Required, RegularExpression( "^[a-f0-9]{40}$" )] string PublishedSha, [Required, MaxLength( 12000 )] string CommentBody, [Range( 1, long.MaxValue )] long SourceReviewRunId, [Required, MaxLength( 4000 )] string Reason );

		[HttpPost( "{id:long}/record-publication" )]
		public async Task<IActionResult> RecordAsync( [FromRoute( Name = "id" )] long tId, [FromBody] PublicationRequest tPublication, CancellationToken tCancel )
		{
			if ( !tSettings.Value.IsPublicationEnabled )
			{
				return Conflict( new { Error = "Publication is disabled" } );
			}

			try
			{
				ReviewComment.ValidateBody( tPublication.CommentBody, 12000 );
			}
			catch ( InvalidOperationException tException )
			{
				return BadRequest( new { Error = tException.Message } );
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				PullRequest? tempRequest = await tContext.PullRequest.FindAsync( [ tId ], tCancel );

				if ( tempRequest is null )
				{
					return NotFound();
				}

				// A retry returns the existing delivery without resetting subsequent queue work
				ReviewRun? tempExisting = await tContext.ReviewRun.SingleOrDefaultAsync( x => x.PullRequestId == tId && x.PublishedSha == tPublication.PublishedSha, tCancel );

				if ( tempExisting is not null )
				{
					return tempExisting.CommentBody == tPublication.CommentBody
						? Ok( new { tempExisting.ReviewRunId, tempExisting.PublishedSha, tempExisting.CommentId, Message = "Publication already recorded" } )
						: Conflict( new { Error = "This revision already has a different publication comment; inspect it before changing anything" } );
				}

				if ( tGate.IsRunning( tId ) || tempRequest.WorkerId is not null || tempRequest.ApprovedSha is not null || tempRequest.CleanupRequestedAt is not null || tempRequest.Status is not ( PullRequestStatus.Blocked or PullRequestStatus.ReadyForApproval ) )
				{
					return Conflict( new { Error = "Stop active work before recording a manual publication; saved work is retained" } );
				}

				ReviewRun? tempSource = await tContext.ReviewRun.SingleOrDefaultAsync( x => x.ReviewRunId == tPublication.SourceReviewRunId && x.PullRequestId == tId, tCancel );

				if ( tempSource is null )
				{
					return BadRequest( new { Error = "The source review run must belong to this PR" } );
				}

				GitHubPullRequest tempRemote = await tGitHub.ReadAsync( $"https://github.com/{tempRequest.Repository}/pull/{tempRequest.Number}", tCancel );

				if ( !WorkflowRules.IsSameBranch( tempRequest, tempRemote ) || tempRemote.HeadSha != tPublication.PublishedSha )
				{
					return Conflict( new { Error = "The supplied commit is not the current head of the tracked open PR branch" } );
				}

				string tempKey = $"manual-publication-{tPublication.PublishedSha}";
				ReviewRun tempRun = new()
				{
					PullRequestId = tId,
					WorkerId = tempSource.WorkerId,
					RunKey = tempKey,
					PublishedSha = tPublication.PublishedSha,
					CommentBody = tPublication.CommentBody,
					Details = JsonSerializer.Serialize( new
					{
						key = tempKey,
						manifest = new { kind = "manual_publication", status = "recorded", report_kind = "commit_summary", published_sha = tPublication.PublishedSha, source_review_run_id = tempSource.ReviewRunId, observed_target_sha = tempRemote.TargetSha, recorded_at = DateTimeOffset.UtcNow, reason = tPublication.Reason },
						publication = new { published_sha = tPublication.PublishedSha, body = tPublication.CommentBody },
						result = new { outcome = "not_reviewed", summary = tPublication.Reason, pr_comment = tPublication.CommentBody }
					} )
				};
				tContext.ReviewRun.Add( tempRun );

				// Record the confirmed push without claiming that the unfinished audit passed
				tempRequest.PublishedSha = tPublication.PublishedSha;
				tempRequest.RemoteSha = tPublication.PublishedSha;
				tempRequest.PreparedSha = null;
				tempRequest.PreparedReviewRunId = null;
				tempRequest.ReviewedTargetSha = null;
				tempRequest.ApprovedSha = null;
				tempRequest.SessionId = null;
				tempRequest.Status = PullRequestStatus.WaitingForCi;
				tempRequest.LastError = null;
				ReviewRecovery tempRecovery = new() { Category = "cancelled", Action = "Manual publication recorded; CI and closure are monitored. Automated repairs remain stopped until review or connect is requested" };
				tempRecovery.Save( tempRequest );
				await tContext.SaveChangesAsync( tCancel );

				return Ok( new { tempRun.ReviewRunId, tempRun.PublishedSha, Message = "Confirmed publication recorded; saved comment queued for delivery, no review or checks started" } );
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				return Conflict( new { Error = $"Unable to record publication: {tException.Message}" } );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}
	}
}
