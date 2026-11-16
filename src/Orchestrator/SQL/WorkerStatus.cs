namespace CPR.SQL
{
	// Values are persisted in PostgreSQL; never renumber or reuse them
	public enum WorkerStatus
	{
		Online = 0,
		Offline = 1,
		Paused = 2
	}
}
