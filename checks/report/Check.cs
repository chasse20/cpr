namespace CPR.Checks
{
	public static class Check
	{
		public static int Count { get; private set; }

		public static void That( bool tIsValid, string tMessage )
		{
			if ( !tIsValid )
			{
				throw new InvalidOperationException( tMessage );
			}

			++Count;
		}
	}
}
