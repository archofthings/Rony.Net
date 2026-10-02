using Rony.Helpers;
using Rony.Interfaces;
using Rony.Models;
using Rony.Wrappers;
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
        private TcpListenerWrapper _listener;
        private CancellationTokenSource _cancellation;
        private CancellationTokenSource _acceptCancellation;
        private bool _refusing;
        private AsyncQueue<Message> _messages;

        /// <summary>The address the server listens on.</summary>
        public IPAddress Address { get; set; }

        /// <inheritdoc />
        public int Port { get; set; }

        /// <summary>Whether the server is started.</summary>
        public bool Active
        {
            get
            {
                lock (_syncRoot)
                    return _listener != null && (_refusing || _listener.Active);
            }
        }

        /// <summary>
        /// How the TCP stream is split into messages. Defaults to <see cref="MessageFraming.None"/>.
        /// </summary>
        public IMessageFraming Framing { get; set; } = MessageFraming.None;

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

        public void Start()
        {
            lock (_syncRoot)
            {
                if (_listener != null) return;

                var listener = new TcpListenerWrapper(Address, Port);
                listener.Start();
                // Remember the port the OS picked for port 0, so a restart listens on the same port.
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;

                _listener = listener;
                _cancellation = new CancellationTokenSource();
                _messages = new AsyncQueue<Message>();
                StartAccepting(listener);
            }
        }

        /// <summary>Starts an accept loop for <paramref name="listener"/> that ends with the server or when connections are refused. Call under the lock.</summary>
        private void StartAccepting(TcpListenerWrapper listener)
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
                _listener.Stop();
                _refusing = true;
            }
        }

        /// <inheritdoc />
        /// <exception cref="SocketException">The port could not be bound again.</exception>
        public void AcceptConnections()
        {
            lock (_syncRoot)
            {
                if (_listener == null || !_refusing) return;

                var listener = new TcpListenerWrapper(Address, Port);
                listener.Start();
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
                _listener.Stop();
                _listener = null;
                _refusing = false;
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

        private async Task AcceptLoopAsync(TcpListenerWrapper listener, AsyncQueue<Message> messages, CancellationToken cancellationToken, CancellationToken acceptToken)
        {
            while (!acceptToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
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
                remoteEndPoint = client.Client?.RemoteEndPoint;
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
