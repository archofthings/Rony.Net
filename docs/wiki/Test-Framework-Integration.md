# Test Framework Integration

Rony.Net works with any test framework. Four small packages remove the setup code:

| Package | Framework | Gives you |
|---|---|---|
| `Rony.Net.Xunit` | xUnit v2 | `MockServerTest` base class, `server.LogTo(ITestOutputHelper)` |
| `Rony.Net.Xunit.v3` | xUnit v3 | The same, for xUnit v3's `Xunit.ITestOutputHelper` |
| `Rony.Net.NUnit` | NUnit 3.14 and later | `MockServerTest` base class, `server.LogToTestContext()` |
| `Rony.Net.MSTest` | MSTest 4 | `MockServerTest` base class, `server.LogTo(TestContext)` |

```console
dotnet add package Rony.Net.Xunit     # or Rony.Net.Xunit.v3, Rony.Net.NUnit, or Rony.Net.MSTest
```

**Which xUnit package?** Use `Rony.Net.Xunit` with xUnit v2 (`xunit` 2.x) and `Rony.Net.Xunit.v3` with xUnit v3
(`xunit.v3`). xUnit v3 moved `ITestOutputHelper` from `Xunit.Abstractions` to the `Xunit` namespace, so the v2 package
does not work with it. The two packages have the same types in the same namespace (`Rony.Net.Xunit`); only the
`ITestOutputHelper` type differs, so switching is a package change plus `using Xunit;` instead of
`using Xunit.Abstractions;`. Install only one of them.

Each one brings in `Rony.Net`, so that is the only package you need.

## The `MockServerTest` base class
Derive your test class from `MockServerTest` and use `Server`:

- **A server per test.** `Server` is created and started the first time a test uses it, on a free port.
- **Logging.** The server's [log](Logging-and-Diagnostics) goes to the test's output, so a failing test shows what the server saw.
- **Clean-up.** The server is disposed after every test.
- **Optional strict mode.** Set `VerifyAllRequestsMatchedAfterTest = true` and a test fails when its client sent a
  request without a configured response.
- **Any transport.** Override `CreateListener()` for TLS, UDP or [framing](Connections-and-Framing).

**xUnit**
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

        using var client = await TcpTestClient.ConnectAsync(Server.Port);
        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));

        Server.Should().HaveReceived("PING", Times.Once());
    }
}
```

**NUnit**
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
NUnit uses one instance of a fixture for all its tests, and `MockServerTest` disposes the server in a `[TearDown]`.
To run the tests of one fixture in parallel, add `[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]`.

**MSTest**
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
`MockServerTest` has the `TestContext` property MSTest fills in, and disposes the server in a `[TestCleanup]`.
`Rony.Net.MSTest` needs MSTest 4, which moved `TestContext` to a different assembly. On MSTest 3, use `Rony.Net` on its own with
`server.Log = line => TestContext.WriteLine("{0}", line);`.

## Choosing the transport and strict mode
```csharp
public class LineProtocolTests : MockServerTest
{
    public LineProtocolTests(ITestOutputHelper output) : base(output)
    {
        VerifyAllRequestsMatchedAfterTest = true;   // fail the test if the client sends something unexpected
    }

    protected override IListener CreateListener() => new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") };

    [Fact]
    public async Task Client_gets_the_price()
    {
        Server.Mock.Send("PRICE ACME").Receive("101.25");

        var price = await new QuoteClient(Server.Port, TimeSpan.FromSeconds(1)).GetPriceAsync("ACME");

        Assert.Equal(101.25m, price);
    }
}
```

## Logging without the base class
To keep your own setup, or to share one server between tests, use the extension methods. Call them at the start of
every test, because each test has its own output:

```csharp
public SharedServerWithLogTests(MockServerFixture fixture, ITestOutputHelper output)
{
    _server = fixture.Server.LogTo(output);   // xUnit
    _server.Mock.Reset();
}
```
```csharp
server.LogToTestContext();   // NUnit: call it inside the test or its [SetUp]
server.LogTo(TestContext);   // MSTest
```

Runnable code: [`TestFrameworkSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/TestFrameworkSamples.cs),
and the [NUnit](https://github.com/archofthings/Rony.Net/tree/main/tests/Rony.Net.NUnit.Tests) and
[MSTest](https://github.com/archofthings/Rony.Net/tree/main/tests/Rony.Net.MSTest.Tests) tests.
