using System.Runtime.InteropServices;
using CPR.Worker.Persistence;
using Microsoft.Win32.SafeHandles;

namespace CPR.Worker.Execution
{
	public sealed class WorkerLock : IDisposable
	{
		private readonly SafeFileHandle _file;
		public bool IsAcquired { get; }

		public WorkerLock( string tPath )
		{
			Storage.RequireCanonical( tPath );
			Directory.CreateDirectory( Path.GetDirectoryName( tPath )! );

			// Use the same Linux flock as interactive SSH sessions, without managed sharing locks
			const int OPEN_READ_WRITE_CREATE_CLOSE_ON_EXEC = 2 | 64 | 524288;
			const int USER_READ_WRITE = 384;
			int tempDescriptor = Open( tPath, OPEN_READ_WRITE_CREATE_CLOSE_ON_EXEC, USER_READ_WRITE );
			if ( tempDescriptor < 0 )
			{
				throw new IOException( $"Unable to open worker lock: errno {Marshal.GetLastPInvokeError()}" );
			}
			_file = new( tempDescriptor, true );
			IsAcquired = Flock( tempDescriptor, 2 | 4 ) == 0;
		}

		public void Dispose()
		{
			_file.Dispose();
		}

		[DllImport( "libc", EntryPoint = "open", SetLastError = true )]
		private static extern int Open( string tPath, int tFlags, int tMode );

		[DllImport( "libc", EntryPoint = "flock", SetLastError = true )]
		private static extern int Flock( int tDescriptor, int tOperation );
	}
}
