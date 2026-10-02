# Rony.Net.Xunit.v3

[xUnit](https://xunit.net) (v3) integration for [Rony.Net](https://github.com/archofthings/Rony.Net), the TCP, TLS and UDP mock server for .NET tests.

- `MockServerTest`: a base class that gives every test its own started mock server, writes the server's log to the
  test output, and disposes the server after the test.
- `server.LogTo(output)`: send any server's log to an `ITestOutputHelper`.

For xUnit v3 (`xunit.v3`). xUnit v3 moved `ITestOutputHelper` into the `Xunit` namespace, so this package has the same
types and the same `Rony.Net.Xunit` namespace as [`Rony.Net.Xunit`](https://www.nuget.org/packages/Rony.Net.Xunit),
which is for xUnit v2.

```csharp
using Rony.Net;
using Rony.Net.Xunit;
using Xunit;

public class PingTests : MockServerTest
{
    public PingTests(ITestOutputHelper output) : base(output) { }

    [Fact]
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
