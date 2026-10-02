using Rony.Interfaces;
using Rony.Listeners;
using System;
using Xunit.Abstractions;

namespace Rony.Net.Xunit
{
    /// <summary>
    /// Base class for xUnit tests that use a mock server. <see cref="Server"/> is created and started the first time a
    /// test uses it, writes its log to the test output, and is disposed after the test.
    /// </summary>
    /// <example><code>
    /// public class PingTests : MockServerTest
    /// {
    ///     public PingTests(ITestOutputHelper output) : base(output) { }
    ///
    ///     [Fact]
    ///     public async Task Client_gets_pong()
    ///     {
    ///         Server.Mock.Send("PING").Receive("PONG");
    ///         // ... run the client against 127.0.0.1:Server.Port ...
    ///         Server.Should().HaveReceived("PING", Times.Once());
    ///     }
    /// }
    /// </code></example>
    public abstract class MockServerTest : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private MockServer _server;

        /// <param name="output">The test output; xUnit passes it to your test class constructor.</param>
        protected MockServerTest(ITestOutputHelper output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>The mock server for the current test, started on first use. Its log goes to the test output.</summary>
        protected MockServer Server
        {
            get
            {
                if (_server == null)
                {
                    var server = new MockServer(CreateListener()).LogTo(_output);
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
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc cref="Dispose()"/>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing || _server == null) return;
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
