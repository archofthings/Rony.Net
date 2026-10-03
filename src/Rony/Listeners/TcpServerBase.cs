using Rony.Helpers;
using Rony.Interfaces;
using Rony.Models;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    /// <summary>
    /// Shared logic of the TCP based servers: accepts connections in the background, reads every
    /// connection independently and keeps connections open across requests.
    /// </summary>
    public abstract class TcpServerBase : IFaultInjectionListener
    {
        private readonly object _syncRoot = new object();
        private readonly ConcurrentDictionary<TcpConnection, byte> _connections = new ConcurrentDictionary<TcpConnection, byte>();
        private readonly ConcurrentDictionary<Task, CancellationToken> _background = new ConcurrentDictionary<Task, CancellationToken>();
        private Socket _listener;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _acceptCancellation;
        private bool _refusing;
        private AsyncQueue<Message> _messages;
        private int _maxBufferedBytes;

        /// <summary>The address the server listens on.</summary>
        public IPAddress Address { get; set; }

        /// <inheritdoc />
        public int Port { get; set; }

        /// <summary>
        /// When true and <see cref="Address"/> is an IPv6 address (typically <see cref="IPAddress.IPv6Any"/>), the listening
        /// socket also accepts IPv4 clients (dual-stack); they appear with IPv4-mapped IPv6 addresses such as
        /// <c>::ffff:127.0.0.1</c>. Set it before <see cref="Start"/>; the default is false. <see cref="Start"/> throws an
        /// <see cref="InvalidOperationException"/> when it is true and <see cref="Address"/> is an IPv4 address.
        /// </summary>
        public bool DualMode { get; set; }

        /// <summary>Whether the server is started.</summary>
        public bool Active
        {
            get
            {
                lock (_syncRoot)
                    return _listener != null;
            }
        }

        /// <summary>
        /// How the TCP stream is split into messages. Defaults to <see cref="MessageFraming.None"/>.
        /// </summary>
        public IMessageFraming Framing { get; set; } = MessageFraming.None;

        /// <summary>
        /// The most bytes a connection may buffer while it waits for the rest of a message; 0 (the default) means unlimited.
        /// A connection that exceeds it is closed and reported through <c>ConnectionFailed</c>; other connections are
        /// unaffected. A single message larger than the limit is refused as well. Set it before <see cref="Start"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public int MaxBufferedBytes
        {
            get => _maxBufferedBytes;
            set => _maxBufferedBytes = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The limit must not be negative.");
        }

        /// <summary>
        /// Keep the connection open after a response so the client can send more requests (default).
        /// Set to false to close the connection after every response.
        /// </summary>
        public bool KeepAlive { get; set; } = true;

        /// <inheritdoc />
        public event Action<object, EndPoint> ConnectionOpened;

        /// <inheritdoc />
        public event Action<object> ConnectionClosed;

        /// <inheritdoc />
        public event Action<EndPoint, Exception> ConnectionFailed;

        /// <summary>Creates a server for the given address and port (0 picks a free port on start).</summary>
        protected TcpServerBase(IPAddress address, int port)
        {
            Address = address ?? throw new ArgumentNullException(nameof(address));
            Port = port;
        }

        /// <summary>
        /// Prepares the stream used to talk to a newly accepted client.
        /// </summary>
        protected abstract Task<Stream> OpenStreamAsync(TcpClient client);

        /// <summary>
        /// Whether more received data can be read right away, so a burst is read completely before it is framed.
        /// </summary>
        protected virtual bool HasPendingData(Stream stream) => stream is NetworkStream networkStream && networkStream.DataAvailable;

        /// <summary>
        /// Creates the listening socket, already bound and listening. Called by <see cref="Start"/> and again when
        /// <see cref="AcceptConnections"/> re-binds. Call under the lock.
        /// </summary>
        private protected virtual Socket CreateListeningSocket()
        {
            if (DualMode && Address.AddressFamily != AddressFamily.InterNetworkV6)
                throw new InvalidOperationException("DualMode needs an IPv6 address, for example IPAddress.IPv6Any, but the address is " + Address + ".");

            // TcpListener sets the socket options a restart on the same port relies on (address reuse on Unix).
            var listener = new TcpListener(Address, Port);
            var socket = listener.Server;
            try
            {
                if (DualMode) socket.DualMode = true;
                listener.Start();
                // Remember the port the OS picked for port 0, so a restart listens on the same port.
                Port = ((IPEndPoint)socket.LocalEndPoint).Port;
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>Called under the lock after the listening socket was closed, by <see cref="RefuseConnections"/> or <see cref="Stop"/>. Must not throw.</summary>
        private protected virtual void OnListenerClosed()
        {
        }

        private static TcpClient Wrap(Socket socket)
        {
            TcpClient client;
            try
            {
                client = new TcpClient();
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            // The Client setter does not dispose the placeholder socket it replaces.
            var placeholder = client.Client;
            client.Client = socket;
            placeholder?.Dispose();
            return client;
        }

        /// <summary>The remote end of a socket, or null when the platform has none (for example an unnamed Unix socket client).</summary>
        internal static EndPoint GetRemoteEndPoint(Socket socket)
        {
            try
            {
                return socket?.RemoteEndPoint;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                if (_listener != null) return;

                var listener = CreateListeningSocket();

                _listener = listener;
                _cancellation = new CancellationTokenSource();
                _messages = new AsyncQueue<Message>();
                StartAccepting(listener);
            }
        }

        /// <summary>Starts an accept loop for <paramref name="listener"/> that ends with the server or when connections are refused. Call under the lock.</summary>
        private void StartAccepting(Socket listener)
        {
            _acceptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
            Track(AcceptLoopAsync(listener, _messages, _cancellation.Token, _acceptCancellation.Token), _acceptCancellation.Token);
        }

        /// <inheritdoc />
        /// <exception cref="InvalidOperationException">The server is not started.</exception>
        public void RefuseConnections()
        {
            lock (_syncRoot)
            {
                if (_listener == null) throw new InvalidOperationException("The server is not started.");
                if (_refusing) return;

                // End the accept loop first, so closing the socket is not mistaken for a failed accept.
                _acceptCancellation.Cancel();
                _acceptCancellation.Dispose();
                AcceptPendingConnections(_listener, _messages, _cancellation.Token);
                _listener.Dispose();
                _refusing = true;
                OnListenerClosed();
            }
        }

        /// <summary>
        /// Accepts the connections that completed their connect but wait in the accept queue, so closing the listening
        /// socket does not drop them. A client the accept loop obtained at the same time is handled by the loop itself,
        /// because it tracks every client it gets, also after its accept token was cancelled. Call under the lock.
        /// </summary>
        private void AcceptPendingConnections(Socket listener, AsyncQueue<Message> messages, CancellationToken cancellationToken)
        {
            try
            {
                // Never block while holding the lock, e.g. when the loop takes the last pending connection first.
                listener.Blocking = false;
                while (listener.Poll(0, SelectMode.SelectRead))
                {
                    // Read off the caller's stack: connection events and user callbacks must not run under the lock.
                    var client = Wrap(listener.Accept());
                    Track(Task.Run(() => ReadConnectionAsync(client, messages, cancellationToken)), cancellationToken);
                }
            }
            catch (SocketException)
            {
                // Nothing (more) to accept.
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <inheritdoc />
        /// <exception cref="SocketException">The port could not be bound again.</exception>
        public void AcceptConnections()
        {
            lock (_syncRoot)
            {
                if (_listener == null || !_refusing) return;

                var listener = CreateListeningSocket();
                _listener = listener;
                _refusing = false;
                StartAccepting(listener);
            }
        }

        /// <inheritdoc />
        public byte[] Frame(byte[] message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return Framing.Encode(message);
        }

        /// <inheritdoc />
        public Task SendRawAsync(byte[] data, object sender)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var connection = (TcpConnection)sender;
            if (connection.IsClosed) throw new InvalidOperationException("The connection is closed.");
            return data.Length == 0 ? Task.CompletedTask : connection.WriteAsync(data);
        }

        /// <inheritdoc />
        public Task SendRawAsync(byte[] data, object sender, int chunkSize, TimeSpan delay, CancellationToken cancellationToken)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize), "The chunk size must be greater than zero.");
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay), "The delay must not be negative.");
            var connection = (TcpConnection)sender;
            if (connection.IsClosed) throw new InvalidOperationException("The connection is closed.");
            return data.Length == 0 ? Task.CompletedTask : connection.WriteAsync(data, chunkSize, delay, cancellationToken);
        }

        /// <inheritdoc />
        public Task ResetAsync(object sender)
        {
            ((TcpConnection)sender).Reset();
            return Task.CompletedTask;
        }

        /// <summary>Remembers a background task, until it completes, so <see cref="WaitForBackgroundWorkAsync"/> can wait for it.</summary>
        private void Track(Task task, CancellationToken cancellationToken)
        {
            _background[task] = cancellationToken;
            task.ContinueWith(completed => _background.TryRemove(completed, out _), TaskScheduler.Default);
        }

        /// <summary>
        /// Waits until the accept loop and every connection task of a stopped run (the ones whose token is cancelled)
        /// have ended, so no connection event is raised afterwards. Call it after <see cref="Stop"/>.
        /// </summary>
        internal async Task WaitForBackgroundWorkAsync()
        {
            while (true)
            {
                var pending = _background.Where(entry => entry.Value.IsCancellationRequested && !entry.Key.IsCompleted)
                    .Select(entry => entry.Key).ToArray();
                if (pending.Length == 0) return;

                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A failed connection task is not a failure of the caller.
                }
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_listener == null) return;

                _cancellation.Cancel();
                _acceptCancellation?.Dispose();
                _listener.Dispose();
                _listener = null;
                _refusing = false;
                OnListenerClosed();
            }

            foreach (var connection in _connections.Keys)
                connection.Close();
            _connections.Clear();
        }

        public void Dispose()
        {
            Stop();
        }

        public Task<Message> ReceiveAsync()
        {
            AsyncQueue<Message> messages;
            CancellationToken cancellationToken;
            lock (_syncRoot)
            {
                if (_listener == null) throw new ObjectDisposedException(GetType().Name, "The server is not started.");
                messages = _messages;
                cancellationToken = _cancellation.Token;
            }

            return messages.DequeueAsync(cancellationToken);
        }

        public Task ReplyAsync(string response, object sender)
        {
            return ReplyAsync(response.GetBytes(), sender);
        }

        public async Task ReplyAsync(byte[] response, object sender)
        {
            var connection = (TcpConnection)sender;
            try
            {
                if (response.Length > 0)
                    await connection.WriteAsync(Framing.Encode(response)).ConfigureAwait(false);

                if (!KeepAlive)
                    connection.Close();
            }
            finally
            {
                connection.MessageHandled();
            }
        }

        public Task CloseAsync(object sender)
        {
            ((TcpConnection)sender).Close();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SendAsync(byte[] data, object sender)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var connection = (TcpConnection)sender;
            if (connection.IsClosed) throw new InvalidOperationException("The connection is closed.");
            return data.Length == 0 ? Task.CompletedTask : connection.WriteAsync(Framing.Encode(data));
        }

        /// <inheritdoc />
        public void CompleteWithoutReply(object sender)
        {
            ((TcpConnection)sender).MessageHandled();
        }

        private async Task AcceptLoopAsync(Socket listener, AsyncQueue<Message> messages, CancellationToken cancellationToken, CancellationToken acceptToken)
        {
            while (!acceptToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = Wrap(await listener.AcceptAsync().ConfigureAwait(false));
                }
                catch (Exception) when (acceptToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // For example no file descriptors left: do not spin.
                    try
                    {
                        await Task.Delay(50, acceptToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    continue;
                }

                Track(ReadConnectionAsync(client, messages, cancellationToken), cancellationToken);
            }
        }

        private async Task ReadConnectionAsync(TcpClient client, AsyncQueue<Message> messages, CancellationToken cancellationToken)
        {
            TcpConnection connection = null;
            EndPoint remoteEndPoint = null;
            try
            {
                remoteEndPoint = GetRemoteEndPoint(client.Client);
                Stream stream;
                // A client still in its handshake is not a known connection yet: abort it when the server stops.
                using (cancellationToken.Register(() => client.Dispose()))
                    stream = await OpenStreamAsync(client).ConfigureAwait(false);
                connection = new TcpConnection(client, stream, OnConnectionClosed);
                _connections.TryAdd(connection, 0);
                if (cancellationToken.IsCancellationRequested)
                {
                    connection.Close();
                    return;
                }

                // Raised before reading, so a greeting is sent (and queued) before any response.
                ConnectionOpened?.Invoke(connection, connection.RemoteEndPoint);

                var framing = Framing ?? MessageFraming.None;
                var buffer = new byte[Math.Max(client.ReceiveBufferSize, 1024)];
                var pending = new byte[buffer.Length];
                var pendingLength = 0;

                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;

                    if (pendingLength + read > pending.Length)
                        Array.Resize(ref pending, Math.Max(pending.Length * 2, pendingLength + read));
                    Buffer.BlockCopy(buffer, 0, pending, pendingLength, read);
                    pendingLength += read;

                    var frames = framing.Decode(new ReadOnlySpan<byte>(pending, 0, pendingLength), !HasPendingData(stream), out var consumed);
                    if (consumed > 0)
                    {
                        Buffer.BlockCopy(pending, consumed, pending, 0, pendingLength - consumed);
                        pendingLength -= consumed;
                    }

                    if (_maxBufferedBytes > 0 && pendingLength > _maxBufferedBytes)
                        throw new InvalidDataException($"The connection buffered {pendingLength} bytes without a complete message, more than MaxBufferedBytes ({_maxBufferedBytes}).");

                    foreach (var frame in frames)
                    {
                        connection.MessageQueued();
                        messages.Enqueue(new Message(frame, connection, connection.RemoteEndPoint));
                    }
                }

                connection.ReadCompleted();
            }
            catch (Exception exception)
            {
                // Aborted connection, failed handshake or server stopping: drop this connection only.
                var expected = cancellationToken.IsCancellationRequested || (connection != null && connection.IsClosed);
                if (connection != null)
                    connection.Close();
                else
                    client.Dispose();

                if (!expected)
                    ConnectionFailed?.Invoke(remoteEndPoint, exception);
            }
        }

        private void OnConnectionClosed(TcpConnection connection)
        {
            _connections.TryRemove(connection, out _);
            ConnectionClosed?.Invoke(connection);
        }
    }
}
