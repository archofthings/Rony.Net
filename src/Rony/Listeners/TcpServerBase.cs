using Rony.Helpers;
using Rony.Interfaces;
using Rony.Models;
using Rony.Wrappers;
using System;
using System.Collections.Concurrent;
using System.IO;
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
    public abstract class TcpServerBase : IConnectionListener
    {
        private readonly object _syncRoot = new object();
        private readonly ConcurrentDictionary<TcpConnection, byte> _connections = new ConcurrentDictionary<TcpConnection, byte>();
        private TcpListenerWrapper _listener;
        private CancellationTokenSource _cancellation;
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
                    return _listener != null && _listener.Active;
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
                _ = AcceptLoopAsync(listener, _messages, _cancellation.Token);
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_listener == null) return;

                _cancellation.Cancel();
                _listener.Stop();
                _listener = null;
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

        private async Task AcceptLoopAsync(TcpListenerWrapper listener, AsyncQueue<Message> messages, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
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

                _ = ReadConnectionAsync(client, messages, cancellationToken);
            }
        }

        private async Task ReadConnectionAsync(TcpClient client, AsyncQueue<Message> messages, CancellationToken cancellationToken)
        {
            TcpConnection connection = null;
            EndPoint remoteEndPoint = null;
            try
            {
                remoteEndPoint = client.Client?.RemoteEndPoint;
                var stream = await OpenStreamAsync(client).ConfigureAwait(false);
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
