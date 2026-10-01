# Rony.Net.Xunit

[xUnit](https://xunit.net) (v2) integration for [Rony.Net](https://github.com/archofthings/Rony.Net), the TCP, TLS and UDP mock server for .NET tests.

- `MockServerTest`: a base class that gives every test its own started mock server, writes the server's log to the
  test output, and disposes the server after the test.
- `server.LogTo(output)`: send any server's log to an `ITestOutputHelper`.

```csharp
using Rony.Net;
using Rony.Net.Xunit;
using Xunit;
using Xunit.Abstractions;

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
