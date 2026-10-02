# Rony.Net.NUnit

[NUnit](https://nunit.org) integration for [Rony.Net](https://github.com/archofthings/Rony.Net), the TCP, TLS and UDP mock server for .NET tests.
Works with NUnit 3.14 and later.

- `MockServerTest`: a base class that gives every test its own started mock server, writes the server's log to the
  test output, and disposes the server in `[TearDown]`.
- `server.LogToTestContext()`: send any server's log to the current test's output.

```csharp
using NUnit.Framework;
using Rony.Net;
using Rony.Net.NUnit;

public class PingTests : MockServerTest
{
    [Test]
    public async Task Client_gets_pong()
    {
        Server.Mock.Send("PING").Receive("PONG");

        // ... run the code under test against 127.0.0.1:Server.Port ...

        Server.Should().HaveReceived("PING", Times.Once());
    }
}
```

Override `CreateListener()` for TLS, UDP or message framing, and set `VerifyAllRequestsMatchedAfterTest = true` to
fail tests whose client sent a request without a configured response. To run the tests of one fixture in parallel,
add `[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]`.

Documentation: [Test Framework Integration](https://github.com/archofthings/Rony.Net/wiki/Test-Framework-Integration).
