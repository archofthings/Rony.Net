using Rony.Models;
using System;
using System.Linq;

namespace Rony.Net
{
    /// <summary>
    /// Fluent assertions on one <see cref="ClientConnection"/>, returned by <see cref="ClientConnection.Should"/>.
    /// They work like the ones of <see cref="MockServerAssertions"/>, but only requests received on this connection
    /// count. Every method checks right away and throws <see cref="MockVerificationException"/> when the check fails;
    /// chain several checks with <see cref="And"/>.
    /// </summary>
    /// <example><code>
    /// var connection = await server.WaitForConnectionAsync();
    /// connection.Should().HaveReceived("LOGIN bob", Times.Once())
    ///     .And.HaveReceivedInOrder("LOGIN bob", "LIST")
    ///     .And.NotHaveReceived("QUIT");
    /// </code></example>
    public sealed class ClientConnectionAssertions
    {
        private readonly MockServer _server;
        private readonly ClientConnection _connection;

        internal ClientConnectionAssertions(MockServer server, ClientConnection connection)
        {
            _server = server;
            _connection = connection;
        }

        /// <summary>Continues the chain, for readability.</summary>
        public ClientConnectionAssertions And => this;

        /// <summary>The request was received on this connection as many times as <paramref name="times"/> says (at least once by default).</summary>
        public ClientConnectionAssertions HaveReceived(string request, Times? times = null) =>
            HaveReceived((request ?? string.Empty).GetBytes(), times);

        /// <inheritdoc cref="HaveReceived(string, Times?)"/>
        public ClientConnectionAssertions HaveReceived(byte[] request, Times? times = null)
        {
            _server.Mock.VerifyOnConnection(_connection.Id, request, times ?? Times.AtLeastOnce());
            return this;
        }

        /// <summary>Requests on this connection satisfying <paramref name="predicate"/> were received as many times as <paramref name="times"/> says (at least once by default).</summary>
        public ClientConnectionAssertions HaveReceived(Func<ReceivedRequest, bool> predicate, Times? times = null)
        {
            _server.Mock.VerifyOnConnection(_connection.Id, predicate, times ?? Times.AtLeastOnce());
            return this;
        }

        /// <summary>The request was never received on this connection.</summary>
        public ClientConnectionAssertions NotHaveReceived(string request) => HaveReceived(request, Times.Never());

        /// <inheritdoc cref="NotHaveReceived(string)"/>
        public ClientConnectionAssertions NotHaveReceived(byte[] request) => HaveReceived(request, Times.Never());

        /// <summary>No request satisfying <paramref name="predicate"/> was received on this connection.</summary>
        public ClientConnectionAssertions NotHaveReceived(Func<ReceivedRequest, bool> predicate) => HaveReceived(predicate, Times.Never());

        /// <summary>The requests were received on this connection in this order; others may come in between. See <c>Mock.VerifyInOrder</c>.</summary>
        public ClientConnectionAssertions HaveReceivedInOrder(params string[] requests)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            return HaveReceivedInOrder(requests.Select(r => (r ?? string.Empty).GetBytes()).ToArray());
        }

        /// <inheritdoc cref="HaveReceivedInOrder(string[])"/>
        public ClientConnectionAssertions HaveReceivedInOrder(params byte[][] requests)
        {
            _server.Mock.VerifyInOrderOnConnection(_connection.Id, requests);
            return this;
        }

        /// <inheritdoc cref="HaveReceivedInOrder(string[])"/>
        public ClientConnectionAssertions HaveReceivedInOrder(params Func<ReceivedRequest, bool>[] predicates)
        {
            _server.Mock.VerifyInOrderOnConnection(_connection.Id, predicates);
            return this;
        }

        /// <summary>The scenario state of this connection is <paramref name="state"/> (see <see cref="ClientConnection.State"/>).</summary>
        public ClientConnectionAssertions BeInState(string state)
        {
            var actual = _connection.State;
            if (actual != state)
                throw new MockVerificationException($"Expected connection {_connection} to be in state \"{state}\", but it is in state \"{actual}\".");
            return this;
        }

        /// <summary>The connection is still open.</summary>
        public ClientConnectionAssertions BeOpen()
        {
            if (!_connection.IsOpen)
                throw new MockVerificationException($"Expected connection {_connection} to be open, but it is closed.");
            return this;
        }

        /// <summary>The connection is closed, by either side. Use <see cref="ClientConnection.WaitForCloseAsync"/> first if it may still be closing.</summary>
        public ClientConnectionAssertions BeClosed()
        {
            if (_connection.IsOpen)
                throw new MockVerificationException($"Expected connection {_connection} to be closed, but it is open.");
            return this;
        }
    }
}
