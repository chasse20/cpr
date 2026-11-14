using CPR.SQL;

namespace CPR.Service.GitHub
{
	public sealed class ReviewCommentService( IServiceScopeFactory tScopes, GitHubClient tGitHub, IOptions<Settings> tSettings, ILogger<ReviewCommentService> tLogger ) : BackgroundService
	{
		protected override async Task ExecuteAsync( CancellationToken tCancel )
		{
			if ( !tSettings.Value.IsPublicationEnabled )
			{
				return;
			}

			while ( !tCancel.IsCancellationRequested )
			{
				try
				{
					using IServiceScope tempScope = tScopes.CreateScope();
					Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
					DateTimeOffset tempRetryBefore = DateTimeOffset.UtcNow.AddMinutes( -2 );
					List<ReviewRun> tempRuns = await tempContext.ReviewRun.Include( x => x.PullRequest )
						.Where( x => x.PublishedSha != null && x.CommentBody != null && x.CommentId == null && ( x.CommentAttemptedAt == null || x.CommentAttemptedAt < tempRetryBefore ) )
						.OrderBy( x => x.ReviewRunId ).Take( 10 ).ToListAsync( tCancel );

					foreach ( ReviewRun tempRun in tempRuns )
					{
						tempRun.CommentAttemptedAt = DateTimeOffset.UtcNow;
						await tempContext.SaveChangesAsync( tCancel );

						try
						{
							PullRequest tempRequest = tempRun.PullRequest!;
							string tempMarker = $"<!-- cpr-report:{tempRequest.PullRequestId}:{tempRun.WorkerId}:{tempRun.RunKey} -->";
							string tempBody = $"## CPR review report\n\nPublished revision: [{tempRun.PublishedSha![ ..7 ]}](https://github.com/{tempRequest.Repository}/commit/{tempRun.PublishedSha})\n\n{tempRun.CommentBody}\n\n{tempMarker}";

							tempRun.CommentId = await tGitHub.PublishCommentAsync( tempRequest.Repository, tempRequest.Number, tempBody, tempMarker, tCancel );
							tempRun.CommentPublishedAt = DateTimeOffset.UtcNow;
							tempRun.CommentError = null;
						}
						catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
						{
							tempRun.CommentError = tException.Message[ ..Math.Min( 4000, tException.Message.Length ) ];
							tLogger.LogWarning( "PR comment for review {ReviewRunId} failed; retrying: {Reason}", tempRun.ReviewRunId, tempRun.CommentError );
						}

						await tempContext.SaveChangesAsync( tCancel );
					}
				}
				catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
				{
					break;
				}
				catch ( Exception tException )
				{
					tLogger.LogError( tException, "PR comment delivery failed; will retry" );
				}

				await Task.Delay( TimeSpan.FromSeconds( 30 ), tCancel );
			}
		}
	}
}
