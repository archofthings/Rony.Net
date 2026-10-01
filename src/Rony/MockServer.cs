using Rony.Handlers;
using Rony.Interfaces;
using Rony.Models;
using System;
using System.Collections.Generic;
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
        private readonly IListener _listener;
        private readonly object _syncRoot = new object();
        private readonly Dictionary<object, Task> _conversations = new Dictionary<object, Task>();
        private CancellationTokenSource _cancellation;

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

        /// <summary>Creates a mock server on top of <paramref name="listener"/>. Call <see cref="Start"/> to begin listening.</summary>
        public MockServer(IListener listener)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            Mock = new RequestHandler();
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
        }

        /// <summary>Stops the server and releases the listener.</summary>
        public void Dispose()
        {
            Stop();
            _listener.Dispose();
        }

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
                catch (Exception)
                {
                    // A single misbehaving client must not take the whole server down.
                    continue;
                }

                Dispatch(received, cancellationToken);
            }
        }

        /// <summary>
        /// Handles requests from the same sender one after another, so responses on a connection keep
        /// their order even when some of them are delayed. Different senders are handled concurrently.
        /// </summary>
        private void Dispatch(Message received, CancellationToken cancellationToken)
        {
            var key = received.Sender ?? received;
            Task task;
            lock (_conversations)
            {
                // Chain onto the sender's previous request, if it is still being handled.
                task = _conversations.TryGetValue(key, out var previous)
                    ? previous.ContinueWith(_ => HandleAsync(received, cancellationToken), TaskScheduler.Default).Unwrap()
                    : Task.Run(() => HandleAsync(received, cancellationToken));
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
            try
            {
                var step = Mock.Handle(received.Body, received.RemoteEndPoint);
                if (step == null)
                {
                    // Nothing configured: reply empty (UDP gets an empty datagram) and end the conversation.
                    await _listener.ReplyAsync(new byte[0], received.Sender).ConfigureAwait(false);
                    await _listener.CloseAsync(received.Sender).ConfigureAwait(false);
                    return;
                }

                if (step.Delay > TimeSpan.Zero)
                    await Task.Delay(step.Delay, cancellationToken).ConfigureAwait(false);

                if (step.SendsReply)
                    await _listener.ReplyAsync(step.Produce(received.Body), received.Sender).ConfigureAwait(false);

                if (step.Disconnect)
                    await _listener.CloseAsync(received.Sender).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The client may already be gone, or the server is stopping; nothing to do.
            }
        }
    }
}
