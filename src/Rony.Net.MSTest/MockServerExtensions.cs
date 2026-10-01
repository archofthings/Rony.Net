using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Rony.Net.MSTest
{
    /// <summary>MSTest helpers for <see cref="MockServer"/>.</summary>
    public static class MockServerExtensions
    {
        /// <summary>
        /// Writes the server's log to the output of a test. For a server shared between tests, call it at the start
        /// of every test with that test's <see cref="TestContext"/>.
        /// </summary>
        public static MockServer LogTo(this MockServer server, TestContext testContext)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (testContext == null) throw new ArgumentNullException(nameof(testContext));
            server.Log = line => testContext.WriteLine("{0}", line);
            return server;
        }
    }
}
