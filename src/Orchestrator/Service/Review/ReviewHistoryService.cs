using CPR.Service.Queue;
using CPR.Service.SSH;
using CPR.SQL;
using System.Text.Json.Nodes;

namespace CPR.Service.Review
{
	public sealed class ReviewHistoryService( IServiceScopeFactory tScopes, QueueGate tGate, WorkerClient tWorkers )
	{
		public async Task CaptureAsync( Worker tWorker, PullRequest tRequest, CancellationToken tCancel )
		{
			using IServiceScope tempReadScope = tScopes.CreateScope();
			Context tempReadContext = tempReadScope.ServiceProvider.GetRequiredService<Context>();
			List<ReviewRun> tempSavedRuns = await tempReadContext.ReviewRun.AsNoTracking().Where( x => x.PullRequestId == tRequest.PullRequestId && x.WorkerId == tWorker.WorkerId ).ToListAsync( tCancel );
			Dictionary<string, string> tempKnown = [];

			foreach ( ReviewRun tempSaved in tempSavedRuns )
			{
				using JsonDocument tempDetails = JsonDocument.Parse( tempSaved.Details );

				if ( tempDetails.RootElement.TryGetProperty( "fingerprint", out JsonElement tempFingerprint ) && tempFingerprint.ValueKind == JsonValueKind.String )
				{
					tempKnown[ tempSaved.RunKey ] = tempFingerprint.GetString()!;
				}
			}

			JsonElement[] tempRuns = await tWorkers.ReadHistoryAsync( tWorker, tRequest, tempKnown, tCancel );

			if ( tempRuns.Length == 0 )
			{
				return;
			}

			string[] tempKeys = [ .. tempRuns.Select( x => x.GetProperty( "key" ).GetString()! ) ];
			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				Dictionary<string, ReviewRun> tempExisting = await tempContext.ReviewRun.Where( x => x.PullRequestId == tRequest.PullRequestId && x.WorkerId == tWorker.WorkerId && tempKeys.Contains( x.RunKey ) ).ToDictionaryAsync( x => x.RunKey, tCancel );

				foreach ( JsonElement tempRun in tempRuns )
				{
					string tempKey = tempRun.GetProperty( "key" ).GetString() ?? "";

					if ( !Regex.IsMatch( tempKey, "^[A-Za-z0-9_-]{1,128}$" ) )
					{
						throw new InvalidOperationException( "Invalid worker review run key" );
					}

					if ( tempExisting.TryGetValue( tempKey, out ReviewRun? tempSaved ) )
					{
						if ( tempSaved.CommentBody is null )
						{
							tempSaved.Details = tempRun.GetRawText();
						}
						else
						{
							// Backfill capture identity without replacing the frozen publication
							JsonObject tempDetails = JsonNode.Parse( tempSaved.Details )!.AsObject();
							tempDetails[ "fingerprint" ] = tempRun.GetProperty( "fingerprint" ).GetString();
							tempSaved.Details = tempDetails.ToJsonString();
						}
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
