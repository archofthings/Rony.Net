using Rony.Helpers;
using Rony.Interfaces;
using Rony.Listeners;
using Rony.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Net
{
    /// <summary>
    /// A TCP (or TLS) relay between your client and a real server that records everything that goes through it into
    /// <see cref="Recording"/>. Save the recording and replay it in tests with <c>MockServer.Replay</c>. UDP is not supported.
    /// </summary>
    /// <example>
    /// <code>
    /// using var proxy = new RecordingProxy("real.host", 5000) { Framing = MessageFraming.Delimiter("\n") };
    /// proxy.Start();
    /// // ... point the client at proxy.Port and run it ...
    /// proxy.Recording.Save("login.rony.json");
    /// </code>
    /// </example>
    public sealed class RecordingProxy : IDisposable, IAsyncDisposable
    {
        private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(5);
        private const int MaxPendingBytes = 16 * 1024 * 1024;

        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly object _syncRoot = new object();
        private readonly ConcurrentDictionary<Relay, byte> _relays = new ConcurrentDictionary<Relay, byte>();
        private readonly List<TaskCompletionSource<bool>> _idleWaiters = new List<TaskCompletionSource<bool>>();
        private TcpListener _listener;
        private CancellationTokenSource _cancellation;
        private Task _acceptTask;
        private ManualResetEventSlim _acceptStopped;
        private int _accepted;
        private int _open;

        /// <summary>Creates a proxy that listens on 127.0.0.1 (port 0 picks a free port on start) and relays to <paramref name="targetHost"/>:<paramref name="targetPort"/>.</summary>
        public RecordingProxy(string targetHost, int targetPort, int port = 0)
            : this(IPAddress.Loopback, port, targetHost, targetPort)
        {
        }

        /// <summary>Creates a proxy that listens on <paramref name="address"/> and <paramref name="port"/> (0 picks a free port on start) and relays to <paramref name="targetHost"/>:<paramref name="targetPort"/>.</summary>
        public RecordingProxy(IPAddress address, int port, string targetHost, int targetPort)
        {
            Address = address ?? throw new ArgumentNullException(nameof(address));
            _targetHost = targetHost ?? throw new ArgumentNullException(nameof(targetHost));
            _targetPort = targetPort;
            Port = port;
        }

        /// <summary>The address the proxy listens on.</summary>
        public IPAddress Address { get; }

        /// <summary>The port the proxy listens on. When created with port 0, read this after <see cref="Start"/>.</summary>
        public int Port { get; private set; }

        /// <summary>Whether the proxy is started and accepting connections.</summary>
        public bool Active
        {
            get
            {
                lock (_syncRoot)
                    return _listener != null;
            }
        }

        /// <summary>
        /// How both directions are split into recorded messages; use the framing of the protocol to get one recorded message
        /// per protocol message. Defaults to <see cref="MessageFraming.None"/>, where every read is one message. Set it before <see cref="Start"/>.
        /// The bytes themselves are always forwarded unchanged and at once.
        /// </summary>
        public IMessageFraming Framing { get; set; } = MessageFraming.None;

        /// <summary>When set, the proxy speaks TLS to the client with this certificate (it must contain a private key). Default: plain TCP.</summary>
        public X509Certificate Certificate { get; set; }

        /// <summary>Speak TLS to the real server, using the target host name for the handshake. Default: plain TCP.</summary>
        public bool TargetTls { get; set; }

        /// <summary>Validates the certificate of the real server when <see cref="TargetTls"/> is set; null uses the default validation.</summary>
        public RemoteCertificateValidationCallback TargetCertificateValidation { get; set; }

        /// <summary>
        /// Receives a line for every connection, relayed message and error. Exceptions thrown by the callback are ignored.
        /// Do not call <see cref="Stop"/> or <see cref="Dispose"/> from this callback: it runs on a relay that they wait for.
        /// </summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// What was relayed so far. It is always the same instance and fills while traffic flows; reading and saving it
        /// is safe at any time.
        /// </summary>
        public Recording Recording { get; } = new Recording();

        /// <summary>Starts accepting connections. Calling it again while started does nothing.</summary>
        public void Start()
        {
            string started;
            lock (_syncRoot)
            {
                if (_listener != null) return;

                var listener = new TcpListener(Address, Port);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _listener = listener;
                _cancellation = new CancellationTokenSource();
                var cancellationToken = _cancellation.Token;
                started = $"recording proxy listening on {Address}:{Port}, relaying to {_targetHost}:{_targetPort}";
                var acceptStopped = _acceptStopped = new ManualResetEventSlim(false);
                _acceptTask = Task.Run(() => AcceptLoopAsync(listener, acceptStopped, cancellationToken));
            }

            Trace(started);
        }

        /// <summary>
        /// Stops accepting, closes every relayed connection on both sides and waits until they have ended. Calling it again does nothing.
        /// Do not call <see cref="Stop"/> or <see cref="Dispose"/> from the <see cref="Log"/> callback: it runs on a relay that this call waits for.
        /// </summary>
        public void Stop()
        {
            if (!StopCore(out _, out var acceptStopped, out var cancellation)) return;

            acceptStopped.Wait();
            acceptStopped.Dispose();
            while (true)
            {
                var relays = _relays.Keys.ToArray();
                if (relays.Length == 0) break;
                foreach (var relay in relays)
                {
                    relay.Finished.Wait();
                    relay.Finished.Dispose();
                }
            }

            cancellation.Dispose();
        }

        /// <summary>Like <see cref="Stop"/>, without blocking the caller.</summary>
        public Task StopAsync()
        {
            return StopCore(out var accept, out _, out var cancellation) ? WaitForEndAsync(accept, cancellation) : Task.CompletedTask;
        }

        /// <summary>Stops accepting and aborts the relays. False if the proxy was not started.</summary>
        private bool StopCore(out Task accept, out ManualResetEventSlim acceptStopped, out CancellationTokenSource cancellation)
        {
            TcpListener listener;
            lock (_syncRoot)
            {
                if (_listener == null)
                {
                    accept = null;
                    acceptStopped = null;
                    cancellation = null;
                    return false;
                }

                listener = _listener;
                _listener = null;
                listener.Stop();
                cancellation = _cancellation;
                accept = _acceptTask;
                acceptStopped = _acceptStopped;
            }

            // Outside the lock: cancelling runs the Abort callback of every relay.
            cancellation.Cancel();
            foreach (var relay in _relays.Keys)
                relay.Abort();
            return true;
        }

        private async Task WaitForEndAsync(Task accept, CancellationTokenSource cancellation)
        {
            try
            {
                await accept.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The accept loop only fails because the listener was closed.
            }

            while (true)
            {
                var running = new List<Task>();
                foreach (var relay in _relays.Keys)
                    running.Add(relay.Done);
                if (running.Count == 0) break;
                await Task.WhenAll(running).ConfigureAwait(false);
            }

            cancellation.Dispose();
        }

        /// <summary>Stops the proxy.</summary>
        public void Dispose() => Stop();

        /// <summary>Stops the proxy without blocking the caller.</summary>
        public ValueTask DisposeAsync() => new ValueTask(StopAsync());

        /// <summary>
        /// Completes once at least one connection was relayed and every relayed connection has ended, so a test can save a
        /// complete recording without sleeping.
        /// </summary>
        /// <param name="timeout">How long to wait; defaults to 5 seconds.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <exception cref="TimeoutException">A connection is still open after <paramref name="timeout"/>.</exception>
        public async Task WaitForConnectionsClosedAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var limit = timeout ?? DefaultWaitTimeout;
            var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_syncRoot)
            {
                if (_accepted > 0 && _open == 0) return;
                _idleWaiters.Add(waiter);
            }

            try
            {
                using (var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var delay = Task.Delay(limit, delayCancellation.Token);
                    if (await Task.WhenAny(waiter.Task, delay).ConfigureAwait(false) != waiter.Task)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException($"The proxy still had open connections after {limit.TotalMilliseconds:0} ms.");
                    }
                    delayCancellation.Cancel();
                }
            }
            finally
            {
                lock (_syncRoot)
                    _idleWaiters.Remove(waiter);
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, ManualResetEventSlim stopped, CancellationToken cancellationToken)
        {
            try
            {
                await AcceptAsync(listener, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                stopped.Set();
            }
        }

        private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
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

                // Created here, not in the relay, so the recorded connections are in the order they were accepted.
                var relay = new Relay(this, client, Recording.AddConnection());
                lock (_syncRoot)
                {
                    _accepted++;
                    _open++;
                }
                _relays[relay] = 0;
                relay.Start(cancellationToken);
            }
        }

        private void RelayEnded(Relay relay)
        {
            List<TaskCompletionSource<bool>> waiters = null;
            lock (_syncRoot)
            {
                _open--;
                if (_open == 0)
                    waiters = new List<TaskCompletionSource<bool>>(_idleWaiters);
            }

            _relays.TryRemove(relay, out _);
            if (waiters != null)
                foreach (var waiter in waiters)
                    waiter.TrySetResult(true);
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
                // A logger may refuse to write, for example after the test finished; never let that break the proxy.
            }
        }

        /// <summary>One relayed connection: the client side, the target side and the two pumps between them.</summary>
        private sealed class Relay
        {
            private readonly RecordingProxy _proxy;
            private readonly TcpClient _client;
            private readonly RecordedConnection _recorded;
            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly TaskCompletionSource<bool> _done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Appending a message takes this lock, so the recorded order is the order the proxy relayed them.
            private readonly object _recordLock = new object();
            private readonly IMessageFraming _framing;
            private TcpClient _target;
            private Stream _clientStream;
            private Stream _targetStream;
            private int _aborted;
            private int _closer = -1;
            private TimeSpan _closeOffset;

            public Relay(RecordingProxy proxy, TcpClient client, RecordedConnection recorded)
            {
                _proxy = proxy;
                _client = client;
                _recorded = recorded;
                _framing = proxy.Framing ?? MessageFraming.None;
            }

            public Task Done => _done.Task;

            /// <summary>Set when the connection has ended, for the blocking <see cref="Stop"/>.</summary>
            public ManualResetEventSlim Finished { get; } = new ManualResetEventSlim(false);

            private string Label => $"#{_recorded.Id}";

            public void Start(CancellationToken cancellationToken)
            {
                Task.Run(() => RunAsync(cancellationToken));
            }

            /// <summary>Closes both sides, which ends the pumps.</summary>
            public void Abort()
            {
                Interlocked.Exchange(ref _aborted, 1);
                CloseBoth();
            }

            private async Task RunAsync(CancellationToken cancellationToken)
            {
                try
                {
                    using (cancellationToken.Register(Abort))
                    {
                        try
                        {
                            _proxy.Trace($"{Label} client connected from {_client.Client?.RemoteEndPoint}");
                            await ConnectAsync().ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            if (Volatile.Read(ref _aborted) == 0 && !cancellationToken.IsCancellationRequested)
                            {
                                _proxy.Trace($"{Label} failed: {exception.GetType().Name}: {exception.Message}");
                                // The real server could not be reached (or a handshake failed): the client sees the connection end.
                                _closer = (int)RecordedSource.Server;
                                _closeOffset = _clock.Elapsed;
                            }
                            CloseBoth();
                            return;
                        }

                        await Task.WhenAll(
                            PumpAsync(_clientStream, _targetStream, RecordedSource.Client, cancellationToken),
                            PumpAsync(_targetStream, _clientStream, RecordedSource.Server, cancellationToken)).ConfigureAwait(false);
                        CloseBoth();
                    }
                }
                finally
                {
                    // The close event is the last message: written once both pumps have recorded what they still had.
                    if (_closer >= 0)
                    {
                        var source = (RecordedSource)_closer;
                        lock (_recordLock)
                            _recorded.Add(new RecordedMessage(source, null, _closeOffset, true));
                        _proxy.Trace($"{Label} {(source == RecordedSource.Client ? "client" : "server")} closed the connection");
                    }
                    _proxy.Trace($"{Label} connection closed");
                    _proxy.RelayEnded(this);
                    _done.TrySetResult(true);
                    Finished.Set();
                }
            }

            private async Task ConnectAsync()
            {
                var clientStream = (Stream)_client.GetStream();
                _clientStream = clientStream;
                if (_proxy.Certificate != null)
                {
                    var ssl = new SslStream(clientStream, false);
                    _clientStream = ssl;
                    await ssl.AuthenticateAsServerAsync(_proxy.Certificate, false, SslProtocols.None, false).ConfigureAwait(false);
                }

                var target = new TcpClient();
                // A full fence: an Abort that runs now either sees the target and closes it, or is seen by the check below.
                Interlocked.Exchange(ref _target, target);
                if (Volatile.Read(ref _aborted) == 1) throw new OperationCanceledException();
                await target.ConnectAsync(_proxy._targetHost, _proxy._targetPort).ConfigureAwait(false);
                _targetStream = target.GetStream();
                if (_proxy.TargetTls)
                {
                    var ssl = new SslStream(_targetStream, false, _proxy.TargetCertificateValidation);
                    _targetStream = ssl;
                    await ssl.AuthenticateAsClientAsync(_proxy._targetHost).ConfigureAwait(false);
                }

                if (Volatile.Read(ref _aborted) == 1) throw new OperationCanceledException();
                _proxy.Trace($"{Label} connected to {_proxy._targetHost}:{_proxy._targetPort}");
            }

            /// <summary>Copies one direction unchanged and records it. The first side that ends decides who closed the connection.</summary>
            private async Task PumpAsync(Stream from, Stream to, RecordedSource source, CancellationToken cancellationToken)
            {
                var other = source == RecordedSource.Client ? RecordedSource.Server : RecordedSource.Client;
                var buffer = new byte[16 * 1024];
                var pending = new byte[buffer.Length];
                var pendingLength = 0;
                var closer = source;
                var endOfStream = false;
                var framing = _framing;
                try
                {
                    while (true)
                    {
                        var read = await from.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            endOfStream = true;
                            break;
                        }

                        if (pendingLength + read > pending.Length)
                            Array.Resize(ref pending, Math.Max(pending.Length * 2, pendingLength + read));
                        Buffer.BlockCopy(buffer, 0, pending, pendingLength, read);
                        pendingLength += read;

                        // Same burst handling as TcpServerBase.
                        var endOfBurst = !(from is NetworkStream network && network.DataAvailable);
                        IReadOnlyList<byte[]> frames;
                        int consumed;
                        try
                        {
                            frames = framing.Decode(new ReadOnlySpan<byte>(pending, 0, pendingLength), endOfBurst, out consumed);
                            if (frames == null || consumed < 0 || consumed > pendingLength)
                                throw new InvalidDataException("The framing returned an invalid result.");
                        }
                        catch (Exception exception)
                        {
                            // The bytes are still relayed: record what is pending as one raw message and stop framing this direction.
                            _proxy.Trace($"{Label} {(source == RecordedSource.Client ? "client" : "server")} framing failed: {exception.GetType().Name}: {exception.Message}, recording the rest of this direction unframed");
                            framing = MessageFraming.None;
                            frames = new[] { CopyPending(pending, pendingLength) };
                            consumed = pendingLength;
                        }

                        if (consumed > 0)
                        {
                            Buffer.BlockCopy(pending, consumed, pending, 0, pendingLength - consumed);
                            pendingLength -= consumed;
                        }

                        // Recorded before the bytes are forwarded, so a reply is normally recorded after its request. A framing that
                        // holds bytes back until a message is complete can still record a request after a reply to its first bytes.
                        foreach (var frame in frames)
                            Record(source, frame);

                        if (pendingLength > MaxPendingBytes)
                        {
                            _proxy.Trace($"{Label} {(source == RecordedSource.Client ? "client" : "server")}: more than 16 MiB without a complete message, recording the rest of this direction unframed");
                            framing = MessageFraming.None;
                            Record(source, CopyPending(pending, pendingLength));
                            pendingLength = 0;
                        }

                        try
                        {
                            await to.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            await to.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception) when (Volatile.Read(ref _aborted) == 0 && !cancellationToken.IsCancellationRequested)
                        {
                            // The other side is gone.
                            closer = other;
                            break;
                        }
                    }
                }
                catch (Exception exception) when (Volatile.Read(ref _aborted) == 0 && !cancellationToken.IsCancellationRequested)
                {
                    // This side reset the connection or it was closed by the other pump.
                    if (Volatile.Read(ref _closer) < 0)
                        _proxy.Trace($"{Label} {(source == RecordedSource.Client ? "client" : "server")} connection ended: {exception.GetType().Name}: {exception.Message}");
                }
                catch (Exception)
                {
                    // The proxy is stopping.
                }

                // Bytes the framing never completed are recorded as a last message of this side, so nothing is lost.
                if (pendingLength > 0)
                    Record(source, CopyPending(pending, pendingLength));

                if (Volatile.Read(ref _aborted) != 0) return;

                if (Interlocked.CompareExchange(ref _closer, (int)closer, -1) == -1)
                    _closeOffset = _clock.Elapsed;

                // A clean end of stream is passed on as a half-close (plain TCP only) and the opposite pump keeps running.
                if (endOfStream && to is NetworkStream && TryShutdownSend(source == RecordedSource.Client ? _target : _client))
                {
                    _proxy.Trace($"{Label} {(source == RecordedSource.Client ? "client" : "server")} finished sending");
                    return;
                }

                CloseBoth();
            }

            private static byte[] CopyPending(byte[] pending, int length)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(pending, 0, copy, 0, length);
                return copy;
            }

            private static bool TryShutdownSend(TcpClient client)
            {
                try
                {
                    client.Client.Shutdown(SocketShutdown.Send);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            private void Record(RecordedSource source, byte[] body)
            {
                lock (_recordLock)
                    _recorded.Add(new RecordedMessage(source, body, _clock.Elapsed, false));
                _proxy.Trace($"{Label} {(source == RecordedSource.Client ? "client → server" : "server → client")}: {body.Length} bytes {ByteFormatter.Describe(body)}");
            }

            private void CloseBoth()
            {
                Dispose(_clientStream);
                Dispose(_targetStream);
                Dispose(_client);
                Dispose(_target);
            }

            private static void Dispose(IDisposable disposable)
            {
                try
                {
                    disposable?.Dispose();
                }
                catch (Exception)
                {
                    // Already broken; nothing to release.
                }
            }
        }
    }
}
