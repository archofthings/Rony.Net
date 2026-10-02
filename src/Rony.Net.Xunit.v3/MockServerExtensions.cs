using System;
using Xunit;

namespace Rony.Net.Xunit
{
    /// <summary>xUnit helpers for <see cref="MockServer"/>.</summary>
    public static class MockServerExtensions
    {
        /// <summary>
        /// Writes the server's log to an xUnit test output. Lines written after the test finished are dropped.
        /// For a server shared between tests (a class fixture), call it at the start of every test.
        /// </summary>
        public static MockServer LogTo(this MockServer server, ITestOutputHelper output)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (output == null) throw new ArgumentNullException(nameof(output));
            server.Log = output.WriteLine;
            return server;
        }
    }
}
