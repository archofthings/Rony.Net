using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Rony;
using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Recipes
// A realistic example: testing your own client class against a mock server.

/// <summary>
/// The code under test: asks a quote server for a price over a line-based TCP protocol,
/// with a timeout and retries.
/// </summary>
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
        _server.Should().HaveReceived("PRICE ACME", Times.Once());
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
        _server.Should().HaveReceived("PRICE ACME", Times.Exactly(2));
    }

    [Fact]
    public async Task Gives_up_after_three_timeouts()
    {
        _server.Mock.Send("PRICE ACME").NoReply();

        await Assert.ThrowsAsync<TimeoutException>(() => CreateClient().GetPriceAsync("ACME"));

        _server.Should().HaveReceived("PRICE ACME", Times.Exactly(3));
    }

    [Fact]
    public async Task Surfaces_server_errors_without_retrying()
    {
        _server.Mock.Send("PRICE NOPE").Receive("ERR unknown symbol");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().GetPriceAsync("NOPE"));

        Assert.Equal("ERR unknown symbol", error.Message);
        _server.Should().HaveReceived("PRICE NOPE", Times.Once());
    }

    [Fact]
    public async Task Handles_a_slow_server_within_the_timeout()
    {
        _server.Mock.Send("PRICE ACME").Receive("1").After(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1m, await CreateClient().GetPriceAsync("ACME"));
    }
}

/// <summary>
/// One server shared by every test in a class (xUnit class fixture). Starting a server is cheap,
/// so a server per test is usually simpler; share one when your test class has many tests.
/// </summary>
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

    [Fact]
    public async Task Second_test()
    {
        _server.Mock.Send("a").Receive("2");
        using var client = await TcpTestClient.ConnectAsync(_server.Port);
        Assert.Equal("2", await client.SendAndReceiveAsync("a"));
    }
}
