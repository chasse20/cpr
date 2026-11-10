namespace CPR.Service.Queue
{
	public sealed class QueueGate
	{
		public SemaphoreSlim Mutex { get; } = new( 1, 1 );
		public ConcurrentDictionary<long, Task> Running { get; } = new();
	}
}
