namespace CPR.Service.GitHub
{
	public sealed record FeedbackState
	{
		public bool IsInitialized { get; set; }
		public Dictionary<string, string> Seen { get; set; } = [];
		public Dictionary<string, FeedbackComment> Pending { get; set; } = [];
		public FeedbackComment[] Active { get; set; } = [];
		public DateTimeOffset? ChangedAt { get; set; }
		public DateTimeOffset? CompletedCheckSeenAt { get; set; }
		public string? CheckHeadSha { get; set; }
		public string[] LastBatch { get; set; } = [];
		public DateTimeOffset? LastAttemptAt { get; set; }
		public string? LastError { get; set; }

		public void Observe( FeedbackComment[] tComments, DateTimeOffset tNow )
		{
			Dictionary<string, string> tempSeen = tComments.ToDictionary( x => x.Key, x => x.Version );

			foreach ( FeedbackComment tempComment in tComments )
			{
				if ( IsInitialized && ( !Seen.TryGetValue( tempComment.Key, out string? tempVersion ) || tempVersion != tempComment.Version ) )
				{
					Pending[ tempComment.Key ] = tempComment;
					ChangedAt = tNow;
					CompletedCheckSeenAt = null;
				}
			}

			foreach ( string tempKey in Pending.Keys.Where( x => !tempSeen.ContainsKey( x ) ).ToArray() )
			{
				Pending.Remove( tempKey );
			}

			Seen = tempSeen;
			IsInitialized = true;
		}

		public bool IsSettled( bool? tIsCheckComplete, string tHeadSha, DateTimeOffset tNow, int tQuietSeconds, int tFallbackSeconds )
		{
			if ( CheckHeadSha != tHeadSha || tIsCheckComplete != true )
			{
				CompletedCheckSeenAt = null;
			}

			CheckHeadSha = tHeadSha;

			if ( tIsCheckComplete == true )
			{
				CompletedCheckSeenAt ??= tNow;
			}

			return Pending.Count > 0 && ChangedAt is not null && tIsCheckComplete != false &&
				( tIsCheckComplete == true
					? tNow - ChangedAt >= TimeSpan.FromSeconds( tQuietSeconds ) && tNow - CompletedCheckSeenAt >= TimeSpan.FromSeconds( tQuietSeconds )
					: tNow - ChangedAt >= TimeSpan.FromSeconds( tFallbackSeconds ) );
		}
	}
}
