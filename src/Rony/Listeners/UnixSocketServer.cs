using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    /// <summary>
    /// A server on a Unix domain socket (a socket file) with the features of <see cref="TcpServer"/>: persistent connections,
    /// framing, push, fault injection. There is no TLS. <see cref="TcpServerBase.Address"/> is <see cref="IPAddress.None"/>,
    /// <see cref="TcpServerBase.Port"/> is 0 and <see cref="TcpServerBase.DualMode"/> has no effect. A reset
    /// (<c>ResetConnection</c>) just closes the connection, because a Unix socket has no RST.
    /// The socket file is created by <c>Start()</c> and deleted by <c>Stop()</c>/<c>Dispose()</c>; a file which already exists at the path
    /// is never deleted and makes <c>Start()</c> fail with the platform's "address in use" <see cref="SocketException"/>.
    /// <c>Start()</c> throws a <see cref="PlatformNotSupportedException"/> where Unix domain sockets are not available.
    /// </summary>
    public class UnixSocketServer : TcpServerBase
    {
        private bool _createdFile;

        /// <summary>Listens on a new, unique socket file in the temp directory (the equivalent of port 0). Read it from <see cref="Path"/>.</summary>
        public UnixSocketServer() : this(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rony-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock"))
        {
        }

        /// <summary>
        /// Listens on the given socket file path. Unix socket paths are limited to about 104 bytes; a longer path fails at <c>Start()</c>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
        public UnixSocketServer(string path) : base(IPAddress.None, 0)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("The socket path must not be null or empty.", nameof(path));
            Path = path;
        }

        /// <summary>The socket file the server listens on.</summary>
        public string Path { get; }

        /// <inheritdoc />
        protected override Task<Stream> OpenStreamAsync(TcpClient client)
        {
            return Task.FromResult<Stream>(client.GetStream());
        }

        private protected override Socket CreateListeningSocket()
        {
#if NET8_0_OR_GREATER
            if (!Socket.OSSupportsUnixDomainSockets)
                throw new PlatformNotSupportedException("Unix domain sockets are not supported on this platform.");
#endif
            // A re-bind after RefuseConnections: the closed socket left our own file behind.
            DeleteOwnFile();

            Socket socket;
            try
            {
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            }
            catch (SocketException exception)
            {
                throw new PlatformNotSupportedException("Unix domain sockets are not supported on this platform.", exception);
            }

            try
            {
                socket.Bind(new UnixDomainSocketEndPoint(Path));
                _createdFile = true;
                socket.Listen(int.MaxValue);
                return socket;
            }
            catch
            {
                socket.Dispose();
                DeleteOwnFile();
                throw;
            }
        }

        private protected override void OnListenerStopped() => DeleteOwnFile();

        private void DeleteOwnFile()
        {
            if (!_createdFile) return;
            _createdFile = false;
            try
            {
                File.Delete(Path);
            }
            catch (Exception)
            {
                // Already gone or not removable; nothing more to do.
            }
        }
    }
}
