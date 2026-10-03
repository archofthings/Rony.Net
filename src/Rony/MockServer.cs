using Rony.Handlers;
using Rony.Helpers;
using Rony.Interfaces;
using Rony.Listeners;
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
    public class MockServer : IDisposable, IAsyncDisposable
    {
        private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        private static readonly byte[] Empty = new byte[0];

        private readonly IListener _listener;
        private readonly IConnectionListener _connectionListener;
        private readonly IFaultInjectionListener _faultListener;
        private readonly object _syncRoot = new object();
        private bool _refusingConnections;
        private readonly Dictionary<object, Task> _conversations = new Dictionary<object, Task>();
        private readonly Journal<ClientConnection> _connections = new Journal<ClientConnection>();
        private readonly ConcurrentDictionary<object, ClientConnection> _connectionsBySender = new ConcurrentDictionary<object, ClientConnection>();
        private CancellationTokenSource _cancellation;
        private Task _listenTask;
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
            _faultListener = listener as IFaultInjectionListener;
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
            lock (_syncRoot)
            {
                if (_cancellation != null) return;
                _listener.Start();
                _cancellation = new CancellationTokenSource();
                var cancellationToken = _cancellation.Token;

                // Logged inside the lock, so StopAsync cannot complete before this line is written.
                Trace($"listening on {Address}:{Port}");
                _listenTask = Task.Run(() => ListenAsync(cancellationToken));
            }
        }

        /// <summary>
        /// Starts listening, like <see cref="Start"/>, and completes once the server is listening: <see cref="Active"/>
        /// is true, <see cref="Port"/> is the assigned port and a client can connect. Calling it while started does nothing.
        /// </summary>
        /// <param name="cancellationToken">Only checked before starting: an already cancelled token cancels the returned task and the server is not started.</param>
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
            Start();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Adds rules to <see cref="Mock"/> that answer like the recorded server: its greeting, and for every recorded
        /// request the replies that followed it, in recorded order (a request seen several times gets a sequence). It can be
        /// called before or after <see cref="Start"/>. Recorded times are not replayed, and rules that already exist for the same
        /// requests are not replaced (it throws <see cref="ArgumentException"/> for them), so call <c>Mock.Reset()</c> first to replace.
        /// A reply of several messages needs a TCP listener. If it throws, the rules added before the exception stay: call
        /// <c>Mock.Reset()</c> to start over.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="recording"/> is null.</exception>
        /// <exception cref="ArgumentException">A response is already configured for a recorded request or for <c>OnConnect()</c>.</exception>
        public void Replay(Recording recording)
        {
            if (recording == null) throw new ArgumentNullException(nameof(recording));
            // Read when a response is sent, so the framing can still be changed after Replay.
            RecordingReplay.Apply(Mock, recording, payload => _faultListener != null ? _faultListener.Frame(payload) : payload);
        }

        /// <summary>Stops listening, closes open connections and cancels pending delayed responses. Safe to call repeatedly.</summary>
        public void Stop()
        {
            lock (_syncRoot)
            {
                if (_cancellation == null) return;
                _cancellation.Cancel();
                _cancellation = null;
                _refusingConnections = false;
                _listener.Stop();

                // Logged inside the lock, so StopAsync cannot complete before this line is written.
                Trace("stopped");
            }
        }

        /// <summary>
        /// Does what <see cref="Stop"/> does, then waits until the server's background work has ended: the listen loop,
        /// every request, delayed response and greeting in flight, and (TCP) every connection still being opened.
        /// Once the returned task has completed, the server calls no user callback any more (<see cref="Log"/>, response
        /// functions, matcher predicates, connection event handlers) until it is started again. Failures and
        /// cancellation inside the background work do not make it throw.
        /// Safe to call repeatedly, on a server that was never started, and concurrently with <see cref="Stop"/>.
        /// Do not await it from inside one of the server's own callbacks: it would wait for itself.
        /// A custom <see cref="IListener"/> gets the guarantee only for the server's own tasks; the server cannot
        /// wait for work the listener runs itself.
        /// </summary>
        public async Task StopAsync()
        {
            Task listenTask;
            lock (_syncRoot)
            {
                Stop();
                listenTask = _listenTask;
            }

            if (_listener is TcpServerBase tcpServer)
                await tcpServer.WaitForBackgroundWorkAsync().ConfigureAwait(false);
            if (listenTask != null)
                await WaitQuietlyAsync(listenTask).ConfigureAwait(false);

            while (true)
            {
                Task[] pending;
                lock (_conversations)
                    pending = _conversations.Values.Where(task => !task.IsCompleted).ToArray();
                if (pending.Length == 0) break;
                await WaitQuietlyAsync(Task.WhenAll(pending)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Makes new clients fail to connect ("connection refused") while the connections the server has accepted keep
        /// working. A client that has only just connected may not be accepted yet and is then reset, so wait with
        /// <see cref="WaitForConnectionAsync"/> before refusing. <see cref="Active"/> and <see cref="Port"/> are unchanged. Does nothing when already refusing; call
        /// <see cref="AcceptConnections"/> to listen again. <see cref="Stop"/> clears it.
        /// </summary>
        /// <exception cref="InvalidOperationException">The server is not started.</exception>
        /// <exception cref="NotSupportedException">The listener cannot refuse connections (UDP, or a custom listener without <see cref="IFaultInjectionListener"/>).</exception>
        public void RefuseConnections()
        {
            RequireFaultInjection();
            lock (_syncRoot)
            {
                _faultListener.RefuseConnections();
                if (!_refusingConnections) Trace("refusing connections");
                _refusingConnections = true;
            }
        }

        /// <summary>
        /// Listens again on the same port after <see cref="RefuseConnections"/>. Does nothing when connections are not refused.
        /// </summary>
        /// <exception cref="NotSupportedException">The listener cannot refuse connections (UDP, or a custom listener without <see cref="IFaultInjectionListener"/>).</exception>
        /// <exception cref="System.Net.Sockets.SocketException">The port could not be bound again.</exception>
        public void AcceptConnections()
        {
            RequireFaultInjection();
            lock (_syncRoot)
            {
                _faultListener.AcceptConnections();
                if (_refusingConnections) Trace("accepting connections");
                _refusingConnections = false;
            }
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

        /// <summary>
        /// Does what <see cref="StopAsync"/> does, then releases the listener like <see cref="Dispose"/>.
        /// Safe to call after <see cref="Dispose"/> and the other way round. Do not await it from inside one of the
        /// server's own callbacks.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            Dispose();
        }

        private static async Task WaitQuietlyAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Failure or cancellation of a background task is not a failure of StopAsync.
            }
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

        internal async Task ResetAsync(ClientConnection connection)
        {
            RequireFaultInjection();
            Trace($"{Label(connection)} resetting the connection");
            await _faultListener.ResetAsync(connection.Sender).ConfigureAwait(false);
        }

        private void RequireFaultInjection()
        {
            if (_faultListener == null)
                throw new NotSupportedException($"{_listener.GetType().Name} cannot simulate this failure. It is available for TCP servers.");
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
            TlsConnectionInfo tls = null;
            if (_connectionListener is ITlsListener tlsListener)
            {
                try
                {
                    tls = tlsListener.GetTlsInfo(sender);
                }
                catch (Exception)
                {
                    // A broken custom listener must not break the connection; it just has no TLS details.
                }
            }

            var connection = new ClientConnection(this, Interlocked.Increment(ref _lastConnectionId), sender, remoteEndPoint, tls);
            _connectionsBySender[sender] = connection;
            _connections.Record(connection);
            Trace($"{Label(connection)} connected from {(string.IsNullOrEmpty(remoteEndPoint?.ToString()) ? "unknown address" : remoteEndPoint.ToString())}{DescribeTls(tls)}");
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

        private string DescribeTls(TlsConnectionInfo tls)
        {
            if (tls == null || Log == null) return string.Empty;
            try
            {
                var parts = new List<string> { tls.Protocol.ToString() };
                if (tls.ServerName != null) parts.Add($"server name {tls.ServerName}");
                if (tls.ClientCertificate != null) parts.Add($"client certificate {tls.ClientCertificate.Subject}");
                return $" ({string.Join(", ", parts)})";
            }
            catch (Exception)
            {
                // A custom listener may return a disposed certificate; the log line just has no TLS details.
                return string.Empty;
            }
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
                var abort = step.Reset && _faultListener != null;
                if (step.Reset && _faultListener == null)
                    Trace($"{label} the listener cannot reset the connection; closing it instead");

                if (step.SendsReply)
                {
                    var response = step.Produce(request,
                        exception => Trace($"error: the response function for {label} threw {Describe(exception)}; sending an empty response"));
                    var modifiers = step.Modifiers;
                    if (modifiers.Length > 0 && _faultListener == null)
                    {
                        var replayFrames = modifiers.Count(m => m.Kind == ResponseBuilder.ReplayedFramesKind);
                        if (modifiers.Length > replayFrames)
                            Trace($"{label} the listener does not support truncated or corrupted responses; sending the response unmodified");
                        if (replayFrames > 0)
                            Trace($"{label} sends only the first of the replayed messages: the listener cannot add frames");
                    }

                    var chunking = step.Chunking;
                    var chunked = chunking.Size > 0 && response.Length > 0;
                    if (chunked && _faultListener == null)
                        Trace($"{label} the listener cannot send in chunks; sending the response whole");
                    chunked &= _faultListener != null;

                    if (_faultListener != null && ((modifiers.Length > 0 && response.Length > 0) || abort || chunked))
                    {
                        // Write the framed (and modified) bytes ourselves; the request is finished below, as a normal reply would.
                        // An empty response stays empty, as with a normal reply.
                        try
                        {
                            var framed = response.Length > 0 ? _faultListener.Frame(response) : Empty;
                            var tags = string.Empty;
                            var sent = response.Length > 0 ? Modify(modifiers, framed, label, out tags) : Empty;
                            chunked &= sent.Length > 0;
                            if (chunked)
                                await _faultListener.SendRawAsync(sent, sender, chunking.Size, chunking.Delay, cancellationToken).ConfigureAwait(false);
                            else if (sent.Length > 0)
                                await _faultListener.SendRawAsync(sent, sender).ConfigureAwait(false);
                            var chunkNote = string.Empty;
                            if (chunked)
                            {
                                var count = ((long)sent.Length + chunking.Size - 1) / chunking.Size;
                                chunkNote = (tags.Length > 0 ? string.Empty : $"{sent.Length} bytes ") +
                                            $"in {count} chunk{(count == 1 ? string.Empty : "s")} of {chunking.Size} byte{(chunking.Size == 1 ? string.Empty : "s")}, " +
                                            $"{chunking.Delay.TotalMilliseconds:0} ms apart" +
                                            (chunking.BytesPerSecond > 0 ? $" (throttled to {chunking.BytesPerSecond} bytes/s)" : string.Empty) + " ";
                            }
                            Trace($"{label} sent {(kind == null ? string.Empty : kind + " ")}" +
                                  (tags.Length > 0 ? $"{sent.Length} of {framed.Length} bytes ({tags}) " : string.Empty) + chunkNote +
                                  $"{ByteFormatter.Describe(sent)}{delay}");
                            if (abort)
                            {
                                Trace($"{label} resetting the connection");
                                await _faultListener.ResetAsync(sender).ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            // The reply is never completed below, so complete the request here.
                            if (kind == null)
                                _connectionListener?.CompleteWithoutReply(sender);
                            throw;
                        }
                        if (kind == null)
                            await _listener.ReplyAsync(Empty, sender).ConfigureAwait(false);
                    }
                    else
                    {
                        if (kind == null)
                            await _listener.ReplyAsync(response, sender).ConfigureAwait(false);
                        else
                            await _connectionListener.SendAsync(response, sender).ConfigureAwait(false);
                        Trace($"{label} sent {(kind == null ? string.Empty : kind + " ")}{ByteFormatter.Describe(response)}{delay}");
                    }
                }
                else
                {
                    if (abort)
                    {
                        Trace($"{label} resetting the connection{delay}");
                        await _faultListener.ResetAsync(sender).ConfigureAwait(false);
                    }
                    if (kind == null)
                        _connectionListener?.CompleteWithoutReply(sender);
                    if (!step.Disconnect && !step.Reset)
                        Trace($"{label} no reply{delay}");
                }

                if ((step.Disconnect || step.Reset) && !abort)
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

        /// <summary>
        /// Applies the modifiers (truncated, corrupted) in order to a copy of the framed response. If one throws, the
        /// error is logged and the framed response is returned unmodified.
        /// </summary>
        private byte[] Modify((string Kind, Func<byte[], byte[]> Apply)[] modifiers, byte[] framed, string label, out string tags)
        {
            var current = framed;
            var kinds = new List<string>();
            foreach (var modifier in modifiers)
            {
                try
                {
                    current = modifier.Apply((byte[])current.Clone()) ?? Empty;
                    kinds.Add(modifier.Kind);
                }
                catch (Exception exception)
                {
                    Trace($"error: the corrupt function for {label} threw {Describe(exception)}; sending the response unmodified");
                    tags = string.Empty;
                    return framed;
                }
            }

            tags = string.Join(", ", kinds);
            return current;
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
