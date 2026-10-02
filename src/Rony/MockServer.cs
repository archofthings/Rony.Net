using Rony.Handlers;
using Rony.Helpers;
using Rony.Interfaces;
using Rony.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Net
{
    /// <summary>
    /// A mock network server for tests. Wraps a listener (<see cref="Rony.Listeners.TcpServer"/>,
    /// <see cref="Rony.Listeners.TcpServerSsl"/> or <see cref="Rony.Listeners.UdpServer"/>) and answers
    /// requests with the responses configured on <see cref="Mock"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// using var server = new MockServer(new TcpServer(0));
    /// server.Mock.Send("PING").Receive("PONG");
    /// server.Start();
    /// // connect a client to 127.0.0.1:server.Port
    /// </code>
    /// </example>
    public class MockServer : IDisposable
    {
        private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        private static readonly byte[] Empty = new byte[0];

        private readonly IListener _listener;
        private readonly IConnectionListener _connectionListener;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<object, Task> _conversations = new Dictionary<object, Task>();
        private readonly Journal<ClientConnection> _connections = new Journal<ClientConnection>();
        private readonly ConcurrentDictionary<object, ClientConnection> _connectionsBySender = new ConcurrentDictionary<object, ClientConnection>();
        private CancellationTokenSource _cancellation;
        private int _lastConnectionId;

        /// <summary>The address the server listens on.</summary>
        public IPAddress Address => _listener.Address;

        /// <summary>
        /// The port the server listens on. When created with port 0, read this after <see cref="Start"/>
        /// to get the port the operating system assigned.
        /// </summary>
        public int Port => _listener.Port;

        /// <summary>Whether the server is started and listening.</summary>
        public bool Active => _listener.Active;

        /// <summary>
        /// Configures responses (<c>Send(...).Receive(...)</c>), and records and verifies requests.
        /// </summary>
        public RequestHandler Mock { get; set; }

        /// <summary>Every request received so far, oldest first. Shortcut for <c>Mock.ReceivedRequests</c>.</summary>
        public IReadOnlyList<ReceivedRequest> ReceivedRequests => Mock.ReceivedRequests;

        /// <summary>
        /// Receives a line for everything the server does: connections, requests and the rule that matched them,
        /// responses, state changes and errors (for example an exception thrown by a <c>Receive(...)</c> function).
        /// For example <c>server.Log = output.WriteLine</c> with xUnit, or <c>Console.WriteLine</c>.
        /// Exceptions thrown by the callback are ignored.
        /// </summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// Every connection accepted so far, open or closed, oldest first. Always empty for listeners without
        /// connections, such as <see cref="Rony.Listeners.UdpServer"/>.
        /// </summary>
        public IReadOnlyList<ClientConnection> Connections => _connections.Snapshot();

        /// <summary>The connections that are still open, oldest first.</summary>
        public IReadOnlyList<ClientConnection> OpenConnections => Connections.Where(c => c.IsOpen).ToArray();

        /// <summary>Raised when a client connects, before its first request is handled.</summary>
        public event EventHandler<ClientConnection> ConnectionOpened;

        /// <summary>Raised when a connection is closed, by either side.</summary>
        public event EventHandler<ClientConnection> ConnectionClosed;

        /// <summary>Creates a mock server on top of <paramref name="listener"/>. Call <see cref="Start"/> to begin listening.</summary>
        public MockServer(IListener listener)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            Mock = new RequestHandler();

            _connectionListener = listener as IConnectionListener;
            if (_connectionListener != null)
            {
                _connectionListener.ConnectionOpened += OnConnectionOpened;
                _connectionListener.ConnectionClosed += OnConnectionClosed;
                _connectionListener.ConnectionFailed += OnConnectionFailed;
            }
        }

        /// <summary>Starts listening. Calling it again while started does nothing; a stopped server can be started again.</summary>
        public void Start()
        {
            CancellationToken cancellationToken;
            lock (_syncRoot)
            {
                if (_cancellation != null) return;
                _listener.Start();
                _cancellation = new CancellationTokenSource();
                cancellationToken = _cancellation.Token;
            }

            Trace($"listening on {Address}:{Port}");
            Task.Run(() => ListenAsync(cancellationToken));
        }

        /// <summary>Stops listening, closes open connections and cancels pending delayed responses. Safe to call repeatedly.</summary>
        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_cancellation == null) return;
                _cancellation.Cancel();
                _cancellation = null;
                _listener.Stop();
            }

            Trace("stopped");
        }

        /// <summary>Stops the server and releases the listener.</summary>
        public void Dispose()
        {
            Stop();
            if (_connectionListener != null)
            {
                _connectionListener.ConnectionOpened -= OnConnectionOpened;
                _connectionListener.ConnectionClosed -= OnConnectionClosed;
                _connectionListener.ConnectionFailed -= OnConnectionFailed;
            }
            _listener.Dispose();
        }

        /// <summary>Fluent assertions on what the server received, for example <c>server.Should().HaveReceived("PING", Times.Once())</c>.</summary>
        public MockServerAssertions Should() => new MockServerAssertions(this);

        #region Connections

        /// <summary>
        /// Sends a message the clients didn't ask for to every open connection, framed like a response.
        /// Returns how many connections it was sent to.
        /// </summary>
        public Task<int> BroadcastAsync(string message) => BroadcastAsync((message ?? string.Empty).GetBytes());

        /// <inheritdoc cref="BroadcastAsync(string)"/>
        public async Task<int> BroadcastAsync(byte[] message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            RequireConnections();

            var sent = 0;
            foreach (var connection in OpenConnections)
            {
                try
                {
                    await PushAsync(connection, message).ConfigureAwait(false);
                    sent++;
                }
                catch (Exception)
                {
                    // Closed while broadcasting; the others still get the message.
                }
            }

            return sent;
        }

        /// <summary>
        /// Waits for the first connection (including ones already accepted). Throws <see cref="TimeoutException"/>
        /// after <paramref name="timeout"/>, which defaults to 5 seconds.
        /// </summary>
        public Task<ClientConnection> WaitForConnectionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            RequireConnections();
            var actualTimeout = timeout ?? DefaultWaitTimeout;
            return _connections.WaitAsync(
                connections => connections.FirstOrDefault(),
                actualTimeout,
                connections => $"Expected a connection within {actualTimeout}, but none was accepted.",
                cancellationToken);
        }

        /// <summary>Waits until at least <paramref name="count"/> connections have been accepted in total.</summary>
        public Task<IReadOnlyList<ClientConnection>> WaitForConnectionsAsync(int count, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            RequireConnections();
            var actualTimeout = timeout ?? DefaultWaitTimeout;
            return _connections.WaitAsync(
                connections => connections.Count >= count ? connections : null,
                actualTimeout,
                connections => $"Expected {count} connections within {actualTimeout}, but {connections.Count} were accepted." +
                               Environment.NewLine + RequestJournal.Describe(connections),
                cancellationToken);
        }

        /// <summary>
        /// Waits until no accepted connection is open, by either side; returns at once if none is open (also when
        /// none was ever accepted). Throws <see cref="TimeoutException"/> after <paramref name="timeout"/>, which
        /// defaults to 5 seconds. TCP only.
        /// </summary>
        public Task WaitForAllConnectionsClosedAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            RequireConnections();
            var actualTimeout = timeout ?? DefaultWaitTimeout;
            return _connections.WaitAsync(
                connections => connections.Any(c => c.IsOpen) ? null : connections,
                actualTimeout,
                connections =>
                {
                    var open = connections.Where(c => c.IsOpen).ToArray();
                    return $"Expected all connections to be closed within {actualTimeout}, but {open.Length} " +
                           $"{(open.Length == 1 ? "is" : "are")} still open." + Environment.NewLine + RequestJournal.Describe(open);
                },
                cancellationToken);
        }

        /// <summary>
        /// Verifies how many connections the server accepted, for example to check that a client reuses its
        /// connection. Throws <see cref="MockVerificationException"/> otherwise.
        /// </summary>
        public void VerifyConnections(Times times)
        {
            RequireConnections();
            var connections = Connections;
            if (times.Matches(connections.Count)) return;

            var expected = times.Max == 0 ? "no connections" : times.ToString().Replace(" time", " connection");
            throw new MockVerificationException(
                $"Expected {expected}, but the server accepted {connections.Count}." +
                Environment.NewLine + RequestJournal.Describe(connections));
        }

        internal async Task PushAsync(ClientConnection connection, byte[] message)
        {
            if (!connection.IsOpen) throw new InvalidOperationException($"Connection {connection} is closed.");
            await _connectionListener.SendAsync(message, connection.Sender).ConfigureAwait(false);
            Trace($"{Label(connection)} pushed {ByteFormatter.Describe(message)}");
        }

        internal async Task CloseAsync(ClientConnection connection)
        {
            Trace($"{Label(connection)} closing the connection");
            await _listener.CloseAsync(connection.Sender).ConfigureAwait(false);
        }

        internal void RequireConnections()
        {
            if (_connectionListener == null)
                throw new NotSupportedException($"{_listener.GetType().Name} has no connections. Connections are available for TCP servers.");
        }

        private void OnConnectionOpened(object sender, EndPoint remoteEndPoint)
        {
            var connection = new ClientConnection(this, Interlocked.Increment(ref _lastConnectionId), sender, remoteEndPoint);
            _connectionsBySender[sender] = connection;
            _connections.Record(connection);
            Trace($"{Label(connection)} connected from {remoteEndPoint}");
            Raise(ConnectionOpened, connection);

            CancellationToken cancellationToken;
            lock (_syncRoot)
            {
                if (_cancellation == null) return;
                cancellationToken = _cancellation.Token;
            }

            var greeting = Mock.HandleConnect(connection);
            if (greeting.Step == null) return;
            LogStateChange(Label(connection), greeting);
            Dispatch(sender, () => RespondAsync(greeting.Step, Empty, sender, Label(connection), "greeting", cancellationToken));
        }

        private void OnConnectionClosed(object sender)
        {
            if (!_connectionsBySender.TryGetValue(sender, out var connection)) return;
            connection.MarkClosed();
            _connections.NotifyChanged();
            Trace($"{Label(connection)} disconnected");
            Raise(ConnectionClosed, connection);
        }

        private void OnConnectionFailed(EndPoint remoteEndPoint, Exception exception)
        {
            Trace($"connection from {remoteEndPoint?.ToString() ?? "unknown address"} failed: {Describe(exception)}");
        }

        private void Raise(EventHandler<ClientConnection> handler, ClientConnection connection)
        {
            try
            {
                handler?.Invoke(this, connection);
            }
            catch (Exception exception)
            {
                Trace($"error: a connection event handler threw {Describe(exception)}");
            }
        }

        #endregion

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Message received;
                try
                {
                    received = await _listener.ReceiveAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // A single misbehaving client must not take the whole server down.
                    if (!cancellationToken.IsCancellationRequested)
                        Trace($"error: receiving a request failed: {Describe(exception)}");
                    continue;
                }

                Dispatch(received.Sender ?? received, () => HandleAsync(received, cancellationToken));
            }
        }

        /// <summary>
        /// Handles the work for the same sender one after another, so responses on a connection keep
        /// their order even when some of them are delayed. Different senders are handled concurrently.
        /// </summary>
        private void Dispatch(object key, Func<Task> work)
        {
            Task task;
            lock (_conversations)
            {
                // Chain onto the sender's previous request, if it is still being handled.
                task = _conversations.TryGetValue(key, out var previous)
                    ? previous.ContinueWith(_ => work(), TaskScheduler.Default).Unwrap()
                    : Task.Run(work);
                _conversations[key] = task;
            }

            task.ContinueWith(completed =>
            {
                lock (_conversations)
                {
                    if (_conversations.TryGetValue(key, out var current) && current == completed)
                        _conversations.Remove(key);
                }
            }, TaskScheduler.Default);
        }

        private async Task HandleAsync(Message received, CancellationToken cancellationToken)
        {
            ClientConnection connection = null;
            if (received.Sender != null)
                _connectionsBySender.TryGetValue(received.Sender, out connection);
            var label = connection != null ? Label(connection) : received.RemoteEndPoint?.ToString() ?? "client";
            var body = received.Body ?? Empty;

            try
            {
                var result = Mock.Handle(body, received.RemoteEndPoint, connection?.Id,
                    (object)connection ?? received.RemoteEndPoint,
                    (exception, source) => Trace($"error: {source} threw {Describe(exception)}"));
                var showState = result.State != RequestHandler.InitialState && !(result.Matched && result.RuleHasState);
                Trace($"{label} received {ByteFormatter.Describe(body)} " +
                      (result.Matched ? $"(matched {result.Rule}" : "(unmatched") +
                      (showState ? $", state \"{result.State}\")" : ")"));
                LogStateChange(label, result);

                if (result.Step == null)
                {
                    // Nothing configured: reply empty (UDP gets an empty datagram) and end the conversation.
                    Trace($"{label} no response configured: " + (connection != null ? "closing the connection" : "sending an empty response"));
                    await _listener.ReplyAsync(Empty, received.Sender).ConfigureAwait(false);
                    await _listener.CloseAsync(received.Sender).ConfigureAwait(false);
                    return;
                }

                await RespondAsync(result.Step, body, received.Sender, label, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The server is stopping.
            }
            catch (Exception exception)
            {
                // The client may already be gone, or the server is stopping.
                if (!cancellationToken.IsCancellationRequested)
                    Trace($"{label} could not respond: {Describe(exception)}");
            }
        }

        /// <summary>
        /// Carries out one configured reaction: wait, reply (or push a greeting) and disconnect.
        /// <paramref name="kind"/> is "greeting" for <c>OnConnect()</c>, and null for a reply to a request.
        /// </summary>
        private async Task RespondAsync(ResponseStep step, byte[] request, object sender, string label, string kind, CancellationToken cancellationToken)
        {
            try
            {
                if (step.Delay > TimeSpan.Zero)
                    await Task.Delay(step.Delay, cancellationToken).ConfigureAwait(false);

                var delay = step.Delay > TimeSpan.Zero ? $" after {step.Delay.TotalMilliseconds:0} ms" : string.Empty;
                if (step.SendsReply)
                {
                    var response = step.Produce(request,
                        exception => Trace($"error: the response function for {label} threw {Describe(exception)}; sending an empty response"));
                    if (kind == null)
                        await _listener.ReplyAsync(response, sender).ConfigureAwait(false);
                    else
                        await _connectionListener.SendAsync(response, sender).ConfigureAwait(false);
                    Trace($"{label} sent {(kind == null ? string.Empty : kind + " ")}{ByteFormatter.Describe(response)}{delay}");
                }
                else
                {
                    if (kind == null)
                        _connectionListener?.CompleteWithoutReply(sender);
                    if (!step.Disconnect)
                        Trace($"{label} no reply{delay}");
                }

                if (step.Disconnect)
                {
                    Trace($"{label} closing the connection{(step.SendsReply ? string.Empty : delay)}");
                    await _listener.CloseAsync(sender).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The server is stopping.
            }
            catch (Exception exception) when (kind != null)
            {
                if (!cancellationToken.IsCancellationRequested)
                    Trace($"{label} could not send the {kind}: {Describe(exception)}");
            }
        }

        private void LogStateChange(string label, MatchResult result)
        {
            if (result.NextState != null && result.NextState != result.State)
                Trace($"{label} state \"{result.State}\" -> \"{result.NextState}\"");
        }

        private void Trace(string message)
        {
            var log = Log;
            if (log == null) return;
            try
            {
                log($"[Rony {DateTime.Now:HH:mm:ss.fff}] {message}");
            }
            catch (Exception)
            {
                // A logger may refuse to write, for example after the test finished; never let that break the server.
            }
        }

        private static string Label(ClientConnection connection) => $"#{connection.Id}";

        private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
    }
}
