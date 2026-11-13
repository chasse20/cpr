using CPR.SQL;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CPR.Service.Discord
{
	public sealed class DiscordNotificationInterceptor( DiscordSettings tSettings ) : SaveChangesInterceptor
	{
		public override InterceptionResult<int> SavingChanges( DbContextEventData tData, InterceptionResult<int> tResult )
		{
			Enqueue( tData.Context );

			return tResult;
		}

		public override ValueTask<InterceptionResult<int>> SavingChangesAsync( DbContextEventData tData, InterceptionResult<int> tResult, CancellationToken tCancel = default )
		{
			Enqueue( tData.Context );

			return ValueTask.FromResult( tResult );
		}

		private void Enqueue( DbContext? tContext )
		{
			if ( tSettings.IsEnabled && tContext is Context tempContext )
			{
				// Rebuild unsaved events if a caller retries a failed save on this context
				EntityEntry<DiscordNotification>[] tempPending = [ .. tempContext.ChangeTracker.Entries<DiscordNotification>().Where( x => x.State == EntityState.Added ) ];

				foreach ( EntityEntry<DiscordNotification> tempEntry in tempPending )
				{
					tempEntry.State = EntityState.Detached;
				}

				EntityEntry<PullRequest>[] tempChanges = [ .. tempContext.ChangeTracker.Entries<PullRequest>().Where( x => x.State == EntityState.Added || ( x.State == EntityState.Modified && x.Property( y => y.Status ).OriginalValue != x.Entity.Status ) ) ];

				foreach ( EntityEntry<PullRequest> tempEntry in tempChanges )
				{
					PullRequest tempRequest = tempEntry.Entity;
					string? tempDetail = tempRequest.Status is PullRequestStatus.Blocked or PullRequestStatus.CiFailure ? tempRequest.LastError : null;

					tempContext.DiscordNotification.Add( new()
					{
						PullRequest = tempRequest,
						WebhookId = tSettings.GetWebhookId(),
						Status = tempRequest.Status,
						Detail = tempDetail?[ ..Math.Min( 1000, tempDetail.Length ) ]
					} );
				}
			}
		}
	}
}
