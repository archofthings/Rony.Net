using Rony.Models;
using System;

namespace Rony.Net
{
    /// <summary>
    /// Fluent assertions on a <see cref="MockServer"/>, returned by <see cref="MockServer.Should"/>. Every method
    /// checks right away and throws <see cref="MockVerificationException"/> when the check fails; chain several
    /// checks with <see cref="And"/>.
    /// </summary>
    /// <example><code>
    /// server.Should().HaveReceived("LOGIN bob", Times.Once())
    ///     .And.HaveReceivedInOrder("LOGIN bob", "LIST", "QUIT")
    ///     .And.HaveNoUnmatchedRequests();
    /// </code></example>
    public sealed class MockServerAssertions
    {
        private readonly MockServer _server;

        internal MockServerAssertions(MockServer server)
        {
            _server = server;
        }

        /// <summary>Continues the chain, for readability.</summary>
        public MockServerAssertions And => this;

        /// <summary>The request was received as many times as <paramref name="times"/> says (at least once by default).</summary>
        public MockServerAssertions HaveReceived(string request, Times? times = null)
        {
            _server.Mock.Verify(request, times ?? Times.AtLeastOnce());
            return this;
        }

        /// <inheritdoc cref="HaveReceived(string, Times?)"/>
        public MockServerAssertions HaveReceived(byte[] request, Times? times = null)
        {
            _server.Mock.Verify(request, times ?? Times.AtLeastOnce());
            return this;
        }

        /// <summary>Requests satisfying <paramref name="predicate"/> were received as many times as <paramref name="times"/> says (at least once by default).</summary>
        public MockServerAssertions HaveReceived(Func<ReceivedRequest, bool> predicate, Times? times = null)
        {
            _server.Mock.Verify(predicate, times ?? Times.AtLeastOnce());
            return this;
        }

        /// <summary>The request was never received.</summary>
        public MockServerAssertions NotHaveReceived(string request) => HaveReceived(request, Times.Never());

        /// <inheritdoc cref="NotHaveReceived(string)"/>
        public MockServerAssertions NotHaveReceived(byte[] request) => HaveReceived(request, Times.Never());

        /// <summary>No request satisfying <paramref name="predicate"/> was received.</summary>
        public MockServerAssertions NotHaveReceived(Func<ReceivedRequest, bool> predicate) => HaveReceived(predicate, Times.Never());

        /// <summary>The requests were received in this order; others may come in between. See <c>Mock.VerifyInOrder</c>.</summary>
        public MockServerAssertions HaveReceivedInOrder(params string[] requests)
        {
            _server.Mock.VerifyInOrder(requests);
            return this;
        }

        /// <inheritdoc cref="HaveReceivedInOrder(string[])"/>
        public MockServerAssertions HaveReceivedInOrder(params byte[][] requests)
        {
            _server.Mock.VerifyInOrder(requests);
            return this;
        }

        /// <inheritdoc cref="HaveReceivedInOrder(string[])"/>
        public MockServerAssertions HaveReceivedInOrder(params Func<ReceivedRequest, bool>[] predicates)
        {
            _server.Mock.VerifyInOrder(predicates);
            return this;
        }

        /// <summary>Every received request had a configured response (strict mode).</summary>
        public MockServerAssertions HaveNoUnmatchedRequests()
        {
            _server.Mock.VerifyAllRequestsMatched();
            return this;
        }

        /// <summary>The server accepted as many connections as <paramref name="times"/> says. TCP only.</summary>
        public MockServerAssertions HaveAcceptedConnections(Times times)
        {
            _server.VerifyConnections(times);
            return this;
        }

        /// <summary>The server-wide scenario state is <paramref name="state"/>. See <c>Mock.InState(...)</c>.</summary>
        public MockServerAssertions BeInState(string state)
        {
            var actual = _server.Mock.State;
            if (actual != state)
                throw new MockVerificationException($"Expected the scenario to be in state \"{state}\", but it is in state \"{actual}\".");
            return this;
        }
    }
}
