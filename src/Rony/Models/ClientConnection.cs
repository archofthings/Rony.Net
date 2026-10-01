using Rony.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Models
{
    /// <summary>
    /// A client connection the mock server accepted (TCP and TCP + SSL/TLS). Use it to push messages to the client,
    /// close the connection, or check what happened on it. Closed connections stay in
    /// <see cref="MockServer.Connections"/>, so you can still inspect them.
    /// </summary>
    public sealed class ClientConnection
    {
        private readonly MockServer _server;
        private readonly TaskCompletionSource<bool> _closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _syncRoot = new object();
        private DateTimeOffset? _closedAt;

        internal ClientConnection(MockServer server, int id, object sender, EndPoint remoteEndPoint)
        {
            _server = server;
            Id = id;
            Sender = sender;
            RemoteEndPoint = remoteEndPoint;
            ConnectedAt = DateTimeOffset.Now;
        }

        /// <summary>Numbers connections in the order they were accepted, starting at 1. Shown as <c>#1</c> in logs.</summary>
        public int Id { get; }

        /// <summary>The client's address.</summary>
        public EndPoint RemoteEndPoint { get; }

        /// <summary>When the server accepted the connection.</summary>
        public DateTimeOffset ConnectedAt { get; }

        /// <summary>When the connection was closed, by either side; null while it is open.</summary>
        public DateTimeOffset? ClosedAt
        {
            get
            {
                lock (_syncRoot)
                    return _closedAt;
            }
        }

        /// <summary>Whether the connection is still open.</summary>
        public bool IsOpen => !_closed.Task.IsCompleted;

        /// <summary>
        /// The scenario state of this connection with <see cref="StateScope.Connection"/>; the server-wide state otherwise.
        /// </summary>
        public string State => _server.Mock.GetState(this);

        /// <summary>The requests received on this connection, oldest first.</summary>
        public IReadOnlyList<ReceivedRequest> ReceivedRequests => _server.Mock.ReceivedRequests.Where(r => r.ConnectionId == Id).ToArray();

        /// <summary>The listener's handle for this connection.</summary>
        internal object Sender { get; }

        /// <summary>Sends a message the client didn't ask for, such as a notification. It is framed like a response.</summary>
        public Task SendAsync(string message) => SendAsync((message ?? string.Empty).GetBytes());

        /// <inheritdoc cref="SendAsync(string)"/>
        public Task SendAsync(byte[] message) => _server.PushAsync(this, message ?? throw new ArgumentNullException(nameof(message)));

        /// <summary>Closes the connection from the server side.</summary>
        public Task CloseAsync() => _server.CloseAsync(this);

        /// <summary>
        /// Waits until the connection is closed, by either side; returns at once if it already is. Use it to check
        /// that your client closes its connections. Throws <see cref="TimeoutException"/> after
        /// <paramref name="timeout"/>, which defaults to 5 seconds.
        /// </summary>
        public async Task WaitForCloseAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var actualTimeout = timeout ?? TimeSpan.FromSeconds(5);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (actualTimeout != Timeout.InfiniteTimeSpan)
                timeoutSource.CancelAfter(actualTimeout);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = timeoutSource.Token.Register(() => cancelled.TrySetResult(true));

            if (await Task.WhenAny(_closed.Task, cancelled.Task).ConfigureAwait(false) == _closed.Task)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Expected connection {this} to close within {actualTimeout}, but it is still open.");
        }

        internal void MarkClosed()
        {
            lock (_syncRoot)
                _closedAt ??= DateTimeOffset.Now;
            _closed.TrySetResult(true);
        }

        public override string ToString() => $"#{Id} from {RemoteEndPoint?.ToString() ?? "unknown address"} ({(IsOpen ? "open" : "closed")})";
    }
}
