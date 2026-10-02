# Rony.Net.MSTest

[MSTest](https://github.com/microsoft/testfx) integration for [Rony.Net](https://github.com/archofthings/Rony.Net), the TCP, TLS and UDP mock server for .NET tests.
Requires MSTest 4. On MSTest 3, set `server.Log = line => TestContext.WriteLine("{0}", line);` yourself.

- `MockServerTest`: a base class that gives every test its own started mock server, writes the server's log to the
  test output, and disposes the server in `[TestCleanup]`.
- `server.LogTo(TestContext)`: send any server's log to a test's output.

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rony.Net;
using Rony.Net.MSTest;

[TestClass]
public class PingTests : MockServerTest
{
    [TestMethod]
    public async Task Client_gets_pong()
    {
        Server.Mock.Send("PING").Receive("PONG");

        // ... run the code under test against 127.0.0.1:Server.Port ...

        Server.Should().HaveReceived("PING", Times.Once());
    }
}
```

Override `CreateListener()` for TLS, UDP or message framing, and set `VerifyAllRequestsMatchedAfterTest = true` to
fail tests whose client sent a request without a configured response.

Documentation: [Test Framework Integration](https://github.com/archofthings/Rony.Net/wiki/Test-Framework-Integration).
