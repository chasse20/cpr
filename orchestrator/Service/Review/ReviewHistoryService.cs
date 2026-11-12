using CPR.Service.Queue;
using CPR.Service.SSH;
using CPR.SQL;

namespace CPR.Service.Review
{
	public sealed class ReviewHistoryService( IServiceScopeFactory tScopes, QueueGate tGate, WorkerClient tWorkers )
	{
		public async Task CaptureAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			JsonElement[] tempRuns = await tWorkers.ReadHistoryAsync( tWorker, tRequest, tCancel );
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				Dictionary<string, ReviewRun> tempExisting = await tempContext.ReviewRun.Where( x => x.PullRequestId == tRequest.PullRequestId && x.WorkerId == tWorker.WorkerId ).ToDictionaryAsync( x => x.RunKey, tCancel );

				foreach ( JsonElement tempRun in tempRuns )
				{
					string tempKey = tempRun.GetProperty( "key" ).GetString() ?? "";

					if ( !Regex.IsMatch( tempKey, "^[A-Za-z0-9_-]{1,128}$" ) )
					{
						throw new InvalidOperationException( "Invalid worker review run key" );
					}

					if ( tempExisting.TryGetValue( tempKey, out ReviewRun? tempSaved ) )
					{
						tempSaved.Details = tempRun.GetRawText();
					}
					else
					{
						tempContext.ReviewRun.Add( new()
						{
							PullRequestId = tRequest.PullRequestId,
							WorkerId = tWorker.WorkerId,
							RunKey = tempKey,
							Details = tempRun.GetRawText()
						} );
					}
				}

				await tempContext.SaveChangesAsync( tCancel );
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}
	}
}
