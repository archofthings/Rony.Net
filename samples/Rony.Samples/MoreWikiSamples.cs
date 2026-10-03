using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Smaller snippets from several wiki pages, kept here so they are compiled and run too.
public class MoreWikiSamples
{
    [Fact]
    public void Every_kind_of_sequence_step()   // Response-Sequences
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("data")
            .Receive("text")                          // text
            .Then(new byte[] { 0x01 })                // bytes
            .Then(text => text.ToUpper())             // computed from the request
            .Then(bytes => bytes.Reverse().ToArray())
            .ThenNoReply()                            // silence
            .ThenDisconnect();                        // close the connection

        Assert.Equal("text", server.Mock.Match("data").GetString());
        Assert.Equal(new byte[] { 0x01 }, server.Mock.Match("data"));
        Assert.Equal("DATA", server.Mock.Match("data").GetString());
        Assert.Equal("atad", server.Mock.Match("data").GetString());
        Assert.Empty(server.Mock.Match("data"));
        Assert.Empty(server.Mock.Match("data"));
    }

    [Fact]
    public void Specific_patterns_first()   // Request-Matching
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send(new Regex("^GET /admin")).Receive("403 Forbidden");
        server.Mock.Send(new Regex("^GET ")).Receive("200 OK");

        Assert.Equal("403 Forbidden", server.Mock.Match("GET /admin/users").GetString());
        Assert.Equal("200 OK", server.Mock.Match("GET /home").GetString());
    }

    [Fact]
    public void Call_count()   // Request-Matching
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("LIST").Receive("a,b,c");
        server.Mock.Match("LIST");
        server.Mock.Match("LIST");

        Assert.Equal(2, server.Mock.Configs["LIST".GetBytes()].CallCount);
    }

    [Fact]
    public void Failure_combinations()   // Simulating-Failures
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("a").NoReply().Then("ok");
        server.Mock.Send("b").Receive("ok").After(TimeSpan.FromSeconds(10)).Then("ok");
        server.Mock.Send("c").Receive("ok").Then("ok").ThenDisconnect();
        server.Mock.Send("d").Receive("ERR 500");
        server.Mock.Send("e").Receive(new byte[] { 0xFF, 0xFF });

        Assert.Empty(server.Mock.Match("a"));
        Assert.Equal("ok", server.Mock.Match("a").GetString());
    }

    [Fact]
    public async Task Waiting_variants()   // Waiting-for-Requests
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Match("BEGIN");
        server.Mock.Match(new byte[] { 0x01 });
        server.Mock.Match("INSERT 1");
        server.Mock.Match("INSERT 2");
        server.Mock.Match("INSERT 3");
        server.Mock.Match("COMMIT");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await server.Mock.WaitForRequestAsync();
        await server.Mock.WaitForRequestAsync(new byte[] { 0x01 });
        await server.Mock.WaitForRequestAsync(r => r.Body.Length > 5);
        await server.Mock.WaitForRequestAsync("COMMIT", TimeSpan.FromSeconds(1), cancellation.Token);

        server.Should().HaveReceived("BEGIN", Times.Once());
        server.Should().HaveReceived(r => r.BodyString.StartsWith("INSERT"), Times.Exactly(3));
    }

    [Fact]
    public async Task Configuration_from_the_port()   // Recipes
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("PING").Receive("PONG");
        server.Start();

        var settings = new Dictionary<string, string>
        {
            ["Quotes:Host"] = "127.0.0.1",
            ["Quotes:Port"] = server.Port.ToString()
        };

        using var client = await TcpTestClient.ConnectAsync(int.Parse(settings["Quotes:Port"]));
        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));
    }

    [Fact]
    public async Task Derived_tcp_server()   // Custom-Listeners
    {
        using var server = new MockServer(new LoggingTcpServer(0));
        server.Mock.Send("ping").Receive("pong");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("pong", await client.SendAndReceiveAsync("ping"));
    }

    [Fact]
    public void Other_constructors()   // Servers, SSL-and-TLS
    {
        using var ipv6 = new TcpServer(IPAddress.IPv6Loopback, 0);
        using var all = new TcpServer("0.0.0.0", 0);
        using var udpAll = new UdpServer(0);
        using var udpEndPoint = new UdpServer(new IPEndPoint(IPAddress.Loopback, 0));
        using var certificate = TestCertificate.CreateSelfSigned();
        using var ssl = new TcpServerSsl("0.0.0.0", 0, certificate, System.Security.Authentication.SslProtocols.None);
        using var sslByName = new TcpServerSsl(0, "localhost", System.Security.Authentication.SslProtocols.None);

        Assert.Equal("0.0.0.0", udpAll.Address.ToString());
    }
}

public class LoggingTcpServer : TcpServerBase
{
    public LoggingTcpServer(int port) : base(IPAddress.Loopback, port) { }

    protected override Task<Stream> OpenStreamAsync(TcpClient client)
    {
        Console.WriteLine($"Client connected from {client.Client.RemoteEndPoint}");
        return Task.FromResult<Stream>(client.GetStream());
    }
}
