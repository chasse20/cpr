using System.Net;
using System.Net.Http.Headers;
using CPR.SQL;

namespace CPR.Service.Discord
{
	public sealed class DiscordNotificationService( IServiceScopeFactory tScopes, IHttpClientFactory tClients, IOptions<Settings> tSettings, ILogger<DiscordNotificationService> tLogger ) : BackgroundService
	{
		private readonly DiscordSettings _settings = tSettings.Value.Discord;
		private DateTimeOffset _nextRequestAt;

		protected override async Task ExecuteAsync( CancellationToken tCancel )
		{
			if ( !_settings.IsEnabled )
			{
				return;
			}

			tLogger.LogInformation( "Discord webhook PR notifications enabled" );

			while ( !tCancel.IsCancellationRequested )
			{
				TimeSpan tempDelay = TimeSpan.FromSeconds( 5 );

				try
				{
					using IServiceScope tempScope = tScopes.CreateScope();
					Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
					DiscordNotification? tempNotification = await tempContext.DiscordNotification.Include( x => x.PullRequest ).Where( x => x.WebhookId == _settings.GetWebhookId() ).OrderBy( x => x.DiscordNotificationId ).FirstOrDefaultAsync( tCancel );

					if ( tempNotification is not null )
					{
						using HttpClient tempClient = tClients.CreateClient( "Discord" );

						await SendAsync( tempClient, tempNotification, tCancel );

						// Delete only after Discord accepts the message; failed delivery survives restarts
						tempContext.DiscordNotification.Remove( tempNotification );
						await tempContext.SaveChangesAsync( tCancel );
						tLogger.LogInformation( "Sent Discord notification {NotificationId} for CPR {PullRequestId}: {Status}", tempNotification.DiscordNotificationId, tempNotification.PullRequestId, tempNotification.Status );
						tempDelay = TimeSpan.Zero;
					}
				}
				catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
				{
					break;
				}
				catch ( DiscordRequestException tException )
				{
					if ( tException.IsPermanent )
					{
						tLogger.LogError( "{DiscordError}; delivery stopped. Correct the webhook configuration and restart the orchestrator. Pending notifications retained", tException.Message );

						return;
					}

					tempDelay = tException.RetryAfter;
					tLogger.LogWarning( "{DiscordError}; retrying in {RetrySeconds} seconds", tException.Message, tempDelay.TotalSeconds );
				}
				catch ( Exception tException )
				{
					tempDelay = TimeSpan.FromSeconds( 60 );
					tLogger.LogWarning( "Discord delivery failed ({ErrorType}); pending notifications retained, retrying in 60 seconds", tException.GetType().Name );
				}

				await Task.Delay( tempDelay, tCancel );
			}
		}

		private async Task SendAsync( HttpClient tClient, DiscordNotification tNotification, CancellationToken tCancel )
		{
			TimeSpan tempWait = _nextRequestAt - DateTimeOffset.UtcNow;

			if ( tempWait > TimeSpan.Zero )
			{
				await Task.Delay( tempWait, tCancel );
			}

			Uri tempUri = new UriBuilder( _settings.GetWebhookUri() ) { Query = "wait=true" }.Uri;
			using HttpResponseMessage tempResponse = await tClient.PostAsJsonAsync( tempUri, new
			{
				content = FormatMessage( tNotification ),
				allowed_mentions = new { parse = Array.Empty<string>() }
			}, tCancel );

			// Respect the webhook's bucket before sending the next queued event
			if ( tempResponse.Headers.TryGetValues( "X-RateLimit-Remaining", out IEnumerable<string>? tempRemaining ) && tempRemaining.FirstOrDefault() == "0" )
			{
				_nextRequestAt = DateTimeOffset.UtcNow + ReadDelay( tempResponse.Headers, "X-RateLimit-Reset-After", TimeSpan.FromSeconds( 1 ) );
			}

			if ( !tempResponse.IsSuccessStatusCode )
			{
				TimeSpan tempRetry = TimeSpan.FromSeconds( (int)tempResponse.StatusCode >= 500 ? 60 : 900 );
				string tempCode = "unknown";

				if ( tempResponse.StatusCode == HttpStatusCode.TooManyRequests )
				{
					tempRetry = ReadDelay( tempResponse.Headers, "Retry-After", TimeSpan.FromSeconds( 60 ) );
				}

				try
				{
					JsonElement tempError = await tempResponse.Content.ReadFromJsonAsync<JsonElement>( tCancel );

					if ( tempError.ValueKind == JsonValueKind.Object && tempError.TryGetProperty( "code", out JsonElement tempErrorCode ) && tempErrorCode.ValueKind == JsonValueKind.Number && tempErrorCode.TryGetInt32( out int tempNumber ) )
					{
						tempCode = tempNumber.ToString( CultureInfo.InvariantCulture );
					}

					if ( tempResponse.StatusCode == HttpStatusCode.TooManyRequests && tempError.ValueKind == JsonValueKind.Object && tempError.TryGetProperty( "retry_after", out JsonElement tempRetryAfter ) && tempRetryAfter.ValueKind == JsonValueKind.Number && tempRetryAfter.TryGetDouble( out double tempSeconds ) && double.IsFinite( tempSeconds ) && tempSeconds > 0 )
					{
						tempRetry = TimeSpan.FromSeconds( Math.Max( tempRetry.TotalSeconds, tempSeconds ) );
					}
				}
				catch ( JsonException )
				{
					// Proxy failures may return HTML instead of Discord's JSON error body
				}

				bool tempIsPermanent = tempResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

				throw new DiscordRequestException( $"Discord webhook returned HTTP {(int)tempResponse.StatusCode}, code {tempCode}", tempRetry, tempIsPermanent );
			}

			// wait=true must return the saved message before its pending event can be removed
			JsonElement tempMessage = await tempResponse.Content.ReadFromJsonAsync<JsonElement>( tCancel );

			if ( tempMessage.ValueKind != JsonValueKind.Object || !tempMessage.TryGetProperty( "id", out JsonElement tempId ) || tempId.ValueKind != JsonValueKind.String || !ulong.TryParse( tempId.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong tempMessageId ) || tempMessageId == 0 )
			{
				throw new InvalidOperationException( "Discord did not confirm a saved webhook message" );
			}
		}

		private static TimeSpan ReadDelay( HttpResponseHeaders tHeaders, string tName, TimeSpan tDefault )
		{
			if ( tHeaders.TryGetValues( tName, out IEnumerable<string>? tempValues ) && double.TryParse( tempValues.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double tempSeconds ) && double.IsFinite( tempSeconds ) && tempSeconds > 0 )
			{
				return TimeSpan.FromSeconds( tempSeconds );
			}

			return tDefault;
		}

		private static string FormatMessage( DiscordNotification tNotification )
		{
			PullRequest tempRequest = tNotification.PullRequest;
			string tempStatus = tNotification.Status switch
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
				_ => tNotification.Status.ToString()
			};
			string tempMessage = $"**CPR {tempRequest.PullRequestId}: {tempStatus}**\nhttps://github.com/{tempRequest.Repository}/pull/{tempRequest.Number}\nRecorded {tNotification.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC. Inspect: `cpr show {tempRequest.PullRequestId}`";

			if ( !string.IsNullOrWhiteSpace( tNotification.Detail ) )
			{
				tempMessage += $"\n\n{tNotification.Detail}";
			}

			return tempMessage.Length <= 2000 ? tempMessage : tempMessage[ ..1997 ] + "...";
		}
	}
}
