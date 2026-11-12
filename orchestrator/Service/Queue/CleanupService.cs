using CPR.Service.Review;
using CPR.Service.SSH;
using CPR.SQL;

namespace CPR.Service.Queue
{
	public sealed class CleanupService( IServiceScopeFactory tScopes, QueueGate tGate, WorkerClient tWorkers, ReviewHistoryService tHistory, IOptions<Settings> tSettings, ILogger<CleanupService> tLogger )
	{
		private readonly SemaphoreSlim _mutex = new( 1, 1 );

		public async Task ScheduleAsync( CancellationToken tCancel )
		{
			if ( !tSettings.Value.IsWorkerExecutionEnabled )
			{
				return;
			}

			await tGate.Mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				List<long> tempIds = await tempContext.PullRequest.AsNoTracking()
					.Where( x => x.CleanupRequestedAt != null && x.CleanedAt == null )
					.OrderBy( x => x.PullRequestId ).Select( x => x.PullRequestId ).ToListAsync( tCancel );

				foreach ( long tempId in tempIds )
				{
					if ( tGate.Running.TryAdd( tempId, Task.CompletedTask ) )
					{
						Task tempTask = Task.Run( () => CleanupAsync( tempId, tCancel ), CancellationToken.None );

						tGate.Running[ tempId ] = tempTask;
						_ = tempTask.ContinueWith( x => { tGate.Running.TryRemove( tempId, out _ ); }, TaskScheduler.Default );
					}
				}
			}
			finally
			{
				tGate.Mutex.Release();
			}
		}

		private async Task CleanupAsync( long tId, CancellationToken tCancel )
		{
			// Sweep one PR at a time so cleanup jobs do not keep each other pending
			await _mutex.WaitAsync( tCancel );

			try
			{
				using IServiceScope tempScope = tScopes.CreateScope();
				Context tempContext = tempScope.ServiceProvider.GetRequiredService<Context>();
				PullRequest? tempRequest = await tempContext.PullRequest.AsNoTracking().SingleOrDefaultAsync( x => x.PullRequestId == tId, tCancel );

				if ( tempRequest is null || tempRequest.CleanupRequestedAt is null || tempRequest.CleanedAt is not null )
				{
					return;
				}

				List<Worker> tempWorkers = await tempContext.Worker.AsNoTracking().Where( x => tempRequest.StorageWorkerIds.Contains( x.WorkerId ) ).OrderBy( x => x.WorkerId == tempRequest.WorkerId ? 0 : 1 ).ThenBy( x => x.WorkerId ).ToListAsync( tCancel );
				List<string> tempErrors = [ .. tempRequest.StorageWorkerIds.Except( tempWorkers.Select( x => x.WorkerId ) ).Select( x => $"Worker {x} is no longer configured; restore it to finish cleanup" ) ];

				foreach ( Worker tempWorker in tempWorkers )
				{
					bool tempIsReserved;
					await tGate.Mutex.WaitAsync( tCancel );

					try
					{
						WorkerStatus tempStatus = await tempContext.Worker.Where( x => x.WorkerId == tempWorker.WorkerId ).Select( x => x.Status ).SingleAsync( tCancel );
						tempIsReserved = tempStatus == WorkerStatus.Online && !tGate.CleaningWorkers.Contains( tempWorker.WorkerId ) && !await tempContext.PullRequest.AnyAsync( x => x.WorkerId == tempWorker.WorkerId && x.PullRequestId != tId, tCancel );

						if ( tempIsReserved )
						{
							tGate.CleaningWorkers.Add( tempWorker.WorkerId );
						}
						else
						{
							tempErrors.Add( $"Worker {tempWorker.WorkerId} is busy, offline, or paused" );
						}
					}
					finally
					{
						tGate.Mutex.Release();
					}

					if ( tempIsReserved )
					{
						try
						{
							await tHistory.CaptureAsync( tempWorker, tempRequest, tCancel );
							await tWorkers.CleanupAsync( tempWorker, tempRequest, tCancel );
							await tGate.Mutex.WaitAsync( tCancel );

							try
							{
								PullRequest tempCurrent = await tempContext.PullRequest.SingleAsync( x => x.PullRequestId == tId, tCancel );

								tempCurrent.StorageWorkerIds = [ .. tempCurrent.StorageWorkerIds.Where( x => x != tempWorker.WorkerId ) ];

								if ( tempCurrent.WorkerId == tempWorker.WorkerId )
								{
									tempCurrent.WorkerId = null;
								}

								if ( tempCurrent.WorkspaceWorkerId == tempWorker.WorkerId )
								{
									tempCurrent.WorkspaceWorkerId = null;
									tempCurrent.SessionId = null;
								}

								await tempContext.SaveChangesAsync( tCancel );
							}
							finally
							{
								tGate.Mutex.Release();
							}
						}
						catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
						{
							tempErrors.Add( $"Worker {tempWorker.WorkerId}: {tException.Message}" );
						}
						finally
						{
							await tGate.Mutex.WaitAsync( CancellationToken.None );

							try
							{
								tGate.CleaningWorkers.Remove( tempWorker.WorkerId );
							}
							finally
							{
								tGate.Mutex.Release();
							}
						}
					}
				}

				try
				{
					DirectoryInfo tempDirectory = new( Path.Combine( tSettings.Value.GitStoragePath, tId.ToString( CultureInfo.InvariantCulture ) ) );

					if ( tempDirectory.LinkTarget is not null )
					{
						tempDirectory.Delete();
					}
					else if ( tempDirectory.Exists )
					{
						tempDirectory.Delete( recursive: true );
					}
				}
				catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
				{
					tempErrors.Add( $"Orchestrator Git storage: {tException.Message}" );
				}

				await tGate.Mutex.WaitAsync( tCancel );

				try
				{
					PullRequest tempCurrent = await tempContext.PullRequest.SingleAsync( x => x.PullRequestId == tId, tCancel );
					string tempError = "Cleanup pending: " + string.Join( "; ", tempErrors );

					if ( tempErrors.Count == 0 && tempCurrent.StorageWorkerIds.Length == 0 )
					{
						tempCurrent.CleanedAt = DateTimeOffset.UtcNow;
						tempCurrent.WorkerId = null;
						tempCurrent.WorkspaceWorkerId = null;
						tempCurrent.SessionId = null;
					}

					tempCurrent.LastError = tempErrors.Count == 0 ? null : tempError[ ..Math.Min( 4000, tempError.Length ) ];
					await tempContext.SaveChangesAsync( tCancel );
				}
				finally
				{
					tGate.Mutex.Release();
				}
			}
			catch ( Exception tException ) when ( !tCancel.IsCancellationRequested )
			{
				tLogger.LogWarning( tException, "File cleanup for PR {Id} failed; will retry", tId );
			}
			finally
			{
				_mutex.Release();
			}
		}
	}
}
