using System.Security.Cryptography;
using CPR.Worker.Execution;

namespace CPR.Worker.Persistence
{
	public static class Storage
	{
		public static readonly JsonSerializerOptions ReportOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true, RespectNullableAnnotations = true };

		public static string ReadText( string tPath )
		{
			return File.Exists( tPath ) ? File.ReadAllText( tPath ).TrimEnd( '\r', '\n' ) : "";
		}

		public static T Read<T>( string tPath, bool tIsReport = false )
		{
			return JsonSerializer.Deserialize<T>( File.ReadAllText( tPath ), tIsReport ? ReportOptions : null ) ?? throw new JsonException( $"Missing JSON in {tPath}" );
		}

		public static JsonObject ReadObject( string tPath )
		{
			return JsonNode.Parse( File.ReadAllText( tPath ) ) as JsonObject ?? throw new JsonException( $"Expected an object in {tPath}" );
		}

		public static void Write( string tPath, object tValue, bool tIsReport = false )
		{
			WriteText( tPath, JsonSerializer.Serialize( tValue, tIsReport ? ReportOptions : null ) + "\n" );
		}

		public static void WriteText( string tPath, string tText )
		{
			Directory.CreateDirectory( Path.GetDirectoryName( tPath )! );
			string tempPath = tPath + ".tmp-" + Guid.NewGuid().ToString( "N" );
			try
			{
				File.WriteAllText( tempPath, tText );
				File.Move( tempPath, tPath, true );
			}
			finally
			{
				File.Delete( tempPath );
			}
		}

		public static void Update( string tPath, Action<JsonObject> tUpdate )
		{
			JsonObject tempValue = ReadObject( tPath );
			tUpdate( tempValue );
			Write( tPath, tempValue );
		}

		public static void RequireCanonical( string tPath )
		{
			WorkerFailure.Require( Path.GetFullPath( tPath ) == tPath, $"Noncanonical path: {tPath}" );
			string? tempPath = tPath;
			while ( tempPath is not null )
			{
				WorkerFailure.Require( new FileInfo( tempPath ).LinkTarget is null, $"Linked path is not allowed: {tempPath}" );
				tempPath = Path.GetDirectoryName( tempPath );
			}
		}

		public static string Evidence( string tArtifacts, string tRelative )
		{
			WorkerFailure.Require( Regex.IsMatch( tRelative, @"^evidence/[A-Za-z0-9_./-]+$" ), $"Invalid evidence path: {tRelative}", "missing_evidence" );
			string tempPath = tArtifacts + "/" + tRelative;
			RequireCanonical( tempPath );
			WorkerFailure.Require( tempPath.StartsWith( tArtifacts + "/evidence/", StringComparison.Ordinal ) && File.Exists( tempPath ), $"Missing regular evidence file: {tRelative}", "missing_evidence" );

			return tempPath;
		}

		public static void CopyDirectory( string tSource, string tDestination )
		{
			RequireCanonical( tSource );
			Directory.CreateDirectory( tDestination );
			foreach ( string tempPath in Directory.EnumerateFileSystemEntries( tSource ) )
			{
				RequireCanonical( tempPath );
				string tempDestination = Path.Combine( tDestination, Path.GetFileName( tempPath ) );
				if ( Directory.Exists( tempPath ) )
				{
					CopyDirectory( tempPath, tempDestination );
				}
				else
				{
					File.Copy( tempPath, tempDestination, true );
				}
			}
		}

		public static string Fingerprint( IEnumerable<string> tPaths )
		{
			using IncrementalHash tempHash = IncrementalHash.CreateHash( HashAlgorithmName.SHA256 );
			foreach ( string tempPath in tPaths.Order( StringComparer.Ordinal ) )
			{
				RequireCanonical( tempPath );
				tempHash.AppendData( Encoding.UTF8.GetBytes( tempPath + "\0" ) );
				using FileStream tempFile = File.OpenRead( tempPath );
				tempHash.AppendData( SHA256.HashData( tempFile ) );
			}

			return Convert.ToHexStringLower( tempHash.GetHashAndReset() );
		}

		public static string CreateRun( string tJob )
		{
			string tempPath = Path.Combine( tJob, "runs", DateTime.UtcNow.ToString( "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture ) + "-" + Guid.NewGuid().ToString( "N" ) );
			Directory.CreateDirectory( tempPath );

			return tempPath;
		}

		public static void Emit( string tMarker, object tValue )
		{
			Console.WriteLine( tMarker + "|" + Convert.ToBase64String( JsonSerializer.SerializeToUtf8Bytes( tValue ) ) );
		}
	}
}
