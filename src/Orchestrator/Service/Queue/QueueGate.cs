namespace CPR.Service.Queue
{
	public sealed class QueueGate( IHostApplicationLifetime tLifetime, ILogger<QueueGate> tLogger )
	{
		private readonly ConcurrentDictionary<long, TaskCompletionSource> _running = new();
		private readonly SemaphoreSlim _schedule = new( 0, 1 );

		public SemaphoreSlim Mutex { get; } = new( 1, 1 );
		public HashSet<int> CleaningWorkers { get; } = [];
		public Dictionary<long, ReviewOperation> Reviews { get; } = [];

		public bool IsRunning( long tId )
		{
			return _running.ContainsKey( tId );
		}

		// Call under Mutex so shutdown and reservations share one boundary
		public bool TryBegin( long tId )
		{
			return !tLifetime.ApplicationStopping.IsCancellationRequested && _running.TryAdd( tId, new( TaskCreationOptions.RunContinuationsAsynchronously ) );
		}

		public void Complete( long tId )
		{
			if ( _running.TryRemove( tId, out TaskCompletionSource? tempCompletion ) )
			{
				tempCompletion.SetResult();
				RequestScheduling();
			}
		}

		public void RequestScheduling()
		{
			lock ( _schedule )
			{
				if ( _schedule.CurrentCount == 0 )
				{
					_schedule.Release();
				}
			}
		}

		public Task<bool> WaitForSchedulingAsync( TimeSpan tDelay, CancellationToken tCancel )
		{
			return _schedule.WaitAsync( tDelay > TimeSpan.Zero ? tDelay : TimeSpan.Zero, tCancel );
		}

		public bool TryRun( long tId, Func<CancellationToken, Task> tOperation )
		{
			if ( !TryBegin( tId ) )
			{
				return false;
			}

			_ = Task.Run( async () =>
			{
				try
				{
					await tOperation( tLifetime.ApplicationStopping );
				}
				catch ( OperationCanceledException ) when ( tLifetime.ApplicationStopping.IsCancellationRequested )
				{
					tLogger.LogInformation( "PR {Id} operation stopped during shutdown", tId );
				}
				catch ( Exception tException )
				{
					tLogger.LogError( tException, "PR {Id} operation failed", tId );
				}
				finally
				{
					Complete( tId );
				}
			}, CancellationToken.None );

			return true;
		}

		public async Task DrainAsync( CancellationToken tCancel )
		{
			Task[] tempTasks;
			await Mutex.WaitAsync( tCancel );

			try
			{
				tempTasks = [ .. _running.Values.Select( x => x.Task ) ];
			}
			finally
			{
				Mutex.Release();
			}

			await Task.WhenAll( tempTasks ).WaitAsync( tCancel );
		}
	}
}
