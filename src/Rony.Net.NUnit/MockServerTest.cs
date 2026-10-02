using NUnit.Framework;
using Rony.Interfaces;
using Rony.Listeners;

namespace Rony.Net.NUnit
{
    /// <summary>
    /// Base class for NUnit tests that use a mock server. <see cref="Server"/> is created and started the first time a
    /// test uses it, writes its log to the test output, and is disposed after the test.
    /// </summary>
    /// <remarks>
    /// NUnit shares one fixture instance between its tests. To run the tests of one fixture in parallel, add
    /// <c>[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]</c> so every test gets its own server.
    /// </remarks>
    /// <example><code>
    /// public class PingTests : MockServerTest
    /// {
    ///     [Test]
    ///     public async Task Client_gets_pong()
    ///     {
    ///         Server.Mock.Send("PING").Receive("PONG");
    ///         // ... run the client against 127.0.0.1:Server.Port ...
    ///         Server.Should().HaveReceived("PING", Times.Once());
    ///     }
    /// }
    /// </code></example>
    public abstract class MockServerTest
    {
        private MockServer _server;

        /// <summary>The mock server for the current test, started on first use. Its log goes to the test output.</summary>
        protected MockServer Server
        {
            get
            {
                if (_server == null)
                {
                    var server = new MockServer(CreateListener()).LogToTestContext();
                    server.Start();
                    _server = server;
                }

                return _server;
            }
        }

        /// <summary>
        /// When true, the test fails if the server received a request without a configured response
        /// (<c>VerifyAllRequestsMatched()</c> runs after the test).
        /// </summary>
        protected bool VerifyAllRequestsMatchedAfterTest { get; set; }

        /// <summary>The listener for <see cref="Server"/>. Defaults to TCP on 127.0.0.1 and a free port; override it for TLS, UDP or framing.</summary>
        protected virtual IListener CreateListener() => new TcpServer(0);

        /// <summary>Checks <see cref="VerifyAllRequestsMatchedAfterTest"/> and disposes the server.</summary>
        [TearDown]
        public void DisposeMockServer()
        {
            if (_server == null) return;
            var server = _server;
            _server = null;
            try
            {
                if (VerifyAllRequestsMatchedAfterTest)
                    server.Mock.VerifyAllRequestsMatched();
            }
            finally
            {
                server.Dispose();
            }
        }
    }
}
