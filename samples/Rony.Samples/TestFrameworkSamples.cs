using Rony.Interfaces;
using Rony.Listeners;
using Rony.Net;
using Rony.Net.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace Rony.Samples;

// Wiki: Test-Framework-Integration (the NUnit and MSTest versions are tested in tests/Rony.Net.NUnit.Tests and tests/Rony.Net.MSTest.Tests)
public class XunitBaseClassSamples : MockServerTest
{
    public XunitBaseClassSamples(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task Client_gets_pong()
    {
        Server.Mock.Send("PING").Receive("PONG");

        using var client = await TcpTestClient.ConnectAsync(Server.Port);
        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));

        Server.Should().HaveReceived("PING", Times.Once());
    }
}

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

public class SharedServerWithLogTests : IClassFixture<MockServerFixture>
{
    private readonly MockServer _server;

    public SharedServerWithLogTests(MockServerFixture fixture, ITestOutputHelper output)
    {
        _server = fixture.Server.LogTo(output);   // every test logs to its own output
        _server.Mock.Reset();
    }

    [Fact]
    public async Task Logs_to_this_test()
    {
        _server.Mock.Send("a").Receive("1");
        using var client = await TcpTestClient.ConnectAsync(_server.Port);
        Assert.Equal("1", await client.SendAndReceiveAsync("a"));
    }
}
