using NUnit.Framework;
using System;

namespace Rony.Net.NUnit
{
    /// <summary>NUnit helpers for <see cref="MockServer"/>.</summary>
    public static class MockServerExtensions
    {
        /// <summary>
        /// Writes the server's log to the output of the current test (<c>TestContext.Out</c>). Call it inside the
        /// test (or its setup); for a server shared between tests, call it at the start of every test.
        /// </summary>
        public static MockServer LogToTestContext(this MockServer server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            // Captured now: the server logs from background threads, outside the test's context.
            var writer = TestContext.Out;
            server.Log = writer.WriteLine;
            return server;
        }
    }
}
