using System.Net;
using System.Net.Sockets;
using Rony;
using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Getting-Started
public class GettingStartedSamples
{
    [Fact]
    public async Task First_test_with_a_plain_TcpClient()
    {
        // 1. Create a server on a free port and tell it what to answer.
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("PING").Receive("PONG");
        server.Start();

        // 2. Talk to it with any client, here a plain TcpClient.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync("PING".GetBytes());

        var buffer = new byte[1024];
        var read = await stream.ReadAsync(buffer);

        // 3. Check the response, and what the client sent.
        Assert.Equal("PONG", buffer[..read].GetString());
        server.Should().HaveReceived("PING", Times.Once());
    }

    [Fact]
    public async Task First_test_with_UDP()
    {
        using var server = new MockServer(new UdpServer("127.0.0.1", 0));
        server.Mock.Send("PING").Receive("PONG");
        server.Start();

        using var client = new UdpClient();
        var request = "PING".GetBytes();
        await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, server.Port));
        var response = await client.ReceiveAsync();

        Assert.Equal("PONG", response.Buffer.GetString());
    }

    [Fact]
    public async Task Same_test_with_the_sample_helper()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("PING").Receive("PONG");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);

        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));
    }
}
