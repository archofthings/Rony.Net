# Recipes

## Test a client class
This is the main use of Rony.Net: testing **your own** network client. Here is a typical client. It asks a quote server
for a price over a line-based protocol, with a timeout and up to three attempts:

```csharp
public sealed class QuoteClient
{
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly int _maxAttempts;

    public QuoteClient(int port, TimeSpan timeout, int maxAttempts = 3)
    {
        _port = port;
        _timeout = timeout;
        _maxAttempts = maxAttempts;
    }

    public async Task<decimal> GetPriceAsync(string symbol)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", _port);
                var stream = client.GetStream();
                await stream.WriteAsync($"PRICE {symbol}\n".GetBytes());

                var line = await ReadLineAsync(stream);
                if (line.StartsWith("ERR")) throw new InvalidOperationException(line);
                return decimal.Parse(line, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is IOException or SocketException or TimeoutException && attempt < _maxAttempts)
            {
                // try again
            }
        }
    }

    private async Task<string> ReadLineAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(_timeout);
        var received = new List<byte>();
        var buffer = new byte[256];
        while (!received.Contains((byte)'\n'))
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("The quote server did not answer in time.");
            }
            if (read == 0) throw new IOException("The quote server closed the connection.");
            received.AddRange(buffer[..read]);
        }
        return received.ToArray().GetString().TrimEnd('\n');
    }
}
```

The tests cover the happy path, pattern matching, retries, timeouts, errors and a slow server, all against a real socket:

```csharp
public class QuoteClientTests : IDisposable
{
    private readonly MockServer _server = new(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });

    public QuoteClientTests() => _server.Start();

    public void Dispose() => _server.Dispose();

    private QuoteClient CreateClient() => new(_server.Port, TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task Returns_the_price()
    {
        _server.Mock.Send("PRICE ACME").Receive("101.25");

        var price = await CreateClient().GetPriceAsync("ACME");

        Assert.Equal(101.25m, price);
        _server.Mock.Verify("PRICE ACME", Times.Once());
    }

    [Fact]
    public async Task Prices_for_any_symbol()
    {
        var prices = new Dictionary<string, string> { ["ACME"] = "10", ["GLOBEX"] = "20" };
        _server.Mock.Send(new Regex(@"^PRICE \w+$")).Receive(request => prices[request.Substring(6)]);

        Assert.Equal(10m, await CreateClient().GetPriceAsync("ACME"));
        Assert.Equal(20m, await CreateClient().GetPriceAsync("GLOBEX"));
    }

    [Fact]
    public async Task Retries_when_the_connection_drops()
    {
        _server.Mock.Send("PRICE ACME").Disconnect().Then("99.5");

        var price = await CreateClient().GetPriceAsync("ACME");

        Assert.Equal(99.5m, price);
        _server.Mock.Verify("PRICE ACME", Times.Exactly(2));
    }

    [Fact]
    public async Task Gives_up_after_three_timeouts()
    {
        _server.Mock.Send("PRICE ACME").NoReply();

        await Assert.ThrowsAsync<TimeoutException>(() => CreateClient().GetPriceAsync("ACME"));

        _server.Mock.Verify("PRICE ACME", Times.Exactly(3));
    }

    [Fact]
    public async Task Surfaces_server_errors_without_retrying()
    {
        _server.Mock.Send("PRICE NOPE").Receive("ERR unknown symbol");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().GetPriceAsync("NOPE"));

        Assert.Equal("ERR unknown symbol", error.Message);
        _server.Mock.Verify("PRICE NOPE", Times.Once());
    }

    [Fact]
    public async Task Handles_a_slow_server_within_the_timeout()
    {
        _server.Mock.Send("PRICE ACME").Receive("1").After(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1m, await CreateClient().GetPriceAsync("ACME"));
    }
}
```

Some points to note:
- The client takes the port (and timeouts) as parameters, so tests can point it at the mock server. Design your
  clients the same way: through constructor parameters, options or configuration.
- `MessageFraming.Delimiter("\n")` matches the protocol, so the `"\n"` is added to every response automatically.
- Short timeouts (300 ms) keep the failure tests fast.

## Less setup with the test framework packages
`Rony.Net.Xunit`, `Rony.Net.NUnit` and `Rony.Net.MSTest` have a `MockServerTest` base class that creates, starts,
logs and disposes a server for every test. See [Test Framework Integration](Test-Framework-Integration).

## Share one server between tests
A server per test is simplest, and starting one is cheap. To share one across a test class, reset it at the start of each test.

**xUnit** (class fixture):
```csharp
public sealed class MockServerFixture : IDisposable
{
    public MockServer Server { get; } = new(new TcpServer(0));

    public MockServerFixture() => Server.Start();

    public void Dispose() => Server.Dispose();
}

public class SharedServerTests : IClassFixture<MockServerFixture>
{
    private readonly MockServer _server;

    public SharedServerTests(MockServerFixture fixture)
    {
        _server = fixture.Server;
        _server.Mock.Reset();   // each test starts from a clean configuration
    }

    [Fact]
    public async Task First_test()
    {
        _server.Mock.Send("a").Receive("1");
        using var client = await TcpTestClient.ConnectAsync(_server.Port);
        Assert.Equal("1", await client.SendAndReceiveAsync("a"));
    }
}
```

**NUnit:**
```csharp
[TestFixture]
public class QuoteClientTests
{
    private MockServer _server;

    [SetUp]
    public void SetUp()
    {
        _server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
        _server.Start();
    }

    [TearDown]
    public void TearDown() => _server.Dispose();

    [Test]
    public async Task Returns_the_price()
    {
        _server.Mock.Send("PRICE ACME").Receive("101.25");

        var price = await new QuoteClient(_server.Port, TimeSpan.FromSeconds(1)).GetPriceAsync("ACME");

        Assert.That(price, Is.EqualTo(101.25m));
        _server.Mock.Verify("PRICE ACME", Times.Once());
    }
}
```

**MSTest:**
```csharp
[TestClass]
public class QuoteClientTests
{
    private MockServer _server;

    [TestInitialize]
    public void Initialize()
    {
        _server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
        _server.Start();
    }

    [TestCleanup]
    public void Cleanup() => _server.Dispose();

    [TestMethod]
    public async Task Returns_the_price()
    {
        _server.Mock.Send("PRICE ACME").Receive("101.25");

        var price = await new QuoteClient(_server.Port, TimeSpan.FromSeconds(1)).GetPriceAsync("ACME");

        Assert.AreEqual(101.25m, price);
        _server.Mock.Verify("PRICE ACME", Times.Once());
    }
}
```

## Run tests in parallel
Use port `0` everywhere and give every test its own server. No two servers then share a port, and xUnit, NUnit or
MSTest can run tests in parallel safely. See [Ports and Lifecycle](Ports-and-Lifecycle).

## Applications that read the address from configuration
Start the server first, then build the configuration from `server.Port`:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("PING").Receive("PONG");
server.Start();

var settings = new Dictionary<string, string>
{
    ["Quotes:Host"] = "127.0.0.1",
    ["Quotes:Port"] = server.Port.ToString()
};
// e.g. new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
// or WebApplicationFactory.WithWebHostBuilder(b => b.UseSetting("Quotes:Port", server.Port.ToString()))
```

## More examples
- [Simulating Failures](Simulating-Failures): retries and timeouts without a client class.
- [Waiting for Requests](Waiting-for-Requests): code that sends in the background.
- [Custom Listeners](Custom-Listeners): mock without a network.
- The [functional tests](https://github.com/archofthings/Rony.Net/tree/main/tests/Rony.FunctionalTests) cover every server type in depth.

Runnable code: [`RecipeSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/RecipeSamples.cs)
