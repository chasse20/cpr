using System.Net;
using CPR.SQL;

namespace CPR.Service.Discord
{
	public sealed class DiscordNotificationService( IServiceScopeFactory tScopes, IHttpClientFactory tClients, IOptions<Settings> tSettings, ILogger<DiscordNotificationService> tLogger ) : BackgroundService
	{
		protected override async Task ExecuteAsync( CancellationToken tCancel )
		{
			if ( !tSettings.Value.Discord.IsEnabled )
			{
				return;
			}

			Uri tempUri = new UriBuilder( tSettings.Value.Discord.GetWebhookUri() ) { Query = "wait=true" }.Uri;
			Dictionary<long, PullRequestStatus>? tempStatuses = null;
			tLogger.LogInformation( "Discord webhook PR notifications enabled" );

			while ( !tCancel.IsCancellationRequested )
			{
				TimeSpan tempDelay = TimeSpan.FromSeconds( 5 );

				try
				{
					using IServiceScope tempScope = tScopes.CreateScope();
					Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
					List<PullRequest> tempRequests = await tempContext.PullRequest.AsNoTracking().OrderBy( x => x.PullRequestId ).ToListAsync( tCancel );

					if ( tempStatuses is null )
					{
						// Start from current statuses without announcing existing history
						tempStatuses = tempRequests.ToDictionary( x => x.PullRequestId, x => x.Status );
					}
					else
					{
						using HttpClient tempClient = tClients.CreateClient( "Discord" );

						foreach ( PullRequest tempRequest in tempRequests )
						{
							if ( !tempStatuses.TryGetValue( tempRequest.PullRequestId, out PullRequestStatus tempStatus ) || tempStatus != tempRequest.Status )
							{
								using HttpResponseMessage tempResponse = await tempClient.PostAsJsonAsync( tempUri, new
								{
									content = FormatMessage( tempRequest ),
									flags = 4, // SUPPRESS_EMBEDS
									allowed_mentions = new { parse = Array.Empty<string>() }
								}, tCancel );

								if ( !tempResponse.IsSuccessStatusCode )
								{
									if ( tempResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound )
									{
										tLogger.LogError( "Discord webhook returned HTTP {StatusCode}; correct the webhook configuration and restart the orchestrator", (int)tempResponse.StatusCode );

										return;
									}

									tempDelay = TimeSpan.FromSeconds( 60 );

									if ( tempResponse.StatusCode == HttpStatusCode.TooManyRequests && tempResponse.Headers.TryGetValues( "Retry-After", out IEnumerable<string>? tempValues ) && double.TryParse( tempValues.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double tempSeconds ) && double.IsFinite( tempSeconds ) && tempSeconds > 0 )
									{
										tempDelay = TimeSpan.FromSeconds( Math.Max( 60, tempSeconds ) );
									}

									tLogger.LogWarning( "Discord webhook returned HTTP {StatusCode}; retrying in {RetrySeconds} seconds", (int)tempResponse.StatusCode, tempDelay.TotalSeconds );

									break;
								}

								tempStatuses[ tempRequest.PullRequestId ] = tempRequest.Status;
								tLogger.LogInformation( "Sent Discord update for CPR {PullRequestId}: {Status}", tempRequest.PullRequestId, tempRequest.Status );
								await Task.Delay( TimeSpan.FromSeconds( 1 ), tCancel );
							}
						}
					}
				}
				catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
				{
					break;
				}
				catch ( Exception tException )
				{
					tempDelay = TimeSpan.FromSeconds( 60 );
					tLogger.LogWarning( "Discord notification check failed ({ErrorType}); retrying in 60 seconds", tException.GetType().Name );
				}

				await Task.Delay( tempDelay, tCancel );
			}
		}

		private static string FormatMessage( PullRequest tRequest )
		{
			string tempStatus = tRequest.Status switch
			{
				PullRequestStatus.WaitingToBeProcessed => "Queued for work",
				PullRequestStatus.Processing => "Work started",
				PullRequestStatus.ReadyForApproval => "Ready for your approval",
				PullRequestStatus.WaitingForCi => "Waiting for CI",
				PullRequestStatus.CiFailure => "CI failed",
				PullRequestStatus.WaitingForMergeTurn => "Waiting for merge turn",
				PullRequestStatus.ReadyToBeMerged => "Ready to merge on GitHub",
				PullRequestStatus.Merged => "Merged",
				PullRequestStatus.Closed => "Closed",
				PullRequestStatus.Blocked => "Blocked; needs your attention",
				PullRequestStatus.Removed => "Tracking stopped",
				_ => tRequest.Status.ToString()
			};
			string tempMessage = $"**CPR {tRequest.PullRequestId}: {tempStatus}**\nhttps://github.com/{tRequest.Repository}/pull/{tRequest.Number}\nObserved {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC. Inspect: `cpr show {tRequest.PullRequestId}`";

			string? tempDetail = tRequest.Status is PullRequestStatus.Blocked or PullRequestStatus.CiFailure ? tRequest.LastError : null;

			if ( !string.IsNullOrWhiteSpace( tempDetail ) )
			{
				tempMessage += $"\n\n{tempDetail[ ..Math.Min( 1000, tempDetail.Length ) ]}";
			}

			return tempMessage.Length <= 2000 ? tempMessage : tempMessage[ ..1997 ] + "...";
		}
	}
}
