using System.Diagnostics;
using System.Net.Sockets;
using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Response-Sequences, Simulating-Failures
public class SequenceAndFailureSamples
{
    [Fact]
    public async Task Different_response_each_time()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("status").Receive("starting").Then("starting").Then("ready");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("starting", await client.SendAndReceiveAsync("status"));
        Assert.Equal("starting", await client.SendAndReceiveAsync("status"));
        Assert.Equal("ready", await client.SendAndReceiveAsync("status"));
        Assert.Equal("ready", await client.SendAndReceiveAsync("status"));   // the last one repeats
    }

    [Fact]
    public async Task Sequence_of_computed_responses()
    {
        var counter = 0;
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("next").Receive(_ => $"#{++counter}");   // a function runs on every call
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("#1", await client.SendAndReceiveAsync("next"));
        Assert.Equal("#2", await client.SendAndReceiveAsync("next"));
    }

    [Fact]
    public async Task Slow_response()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("report").Receive("done").After(TimeSpan.FromMilliseconds(500));
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        var stopwatch = Stopwatch.StartNew();

        Assert.Equal("done", await client.SendAndReceiveAsync("report"));
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(450));
    }

    [Fact]
    public async Task Client_timeout()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("report").NoReply();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("report");

        // TcpTestClient gives up after 5 seconds; your client's own timeout is what you would test here.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReceiveAsync());
    }

    [Fact]
    public async Task Connection_dropped_without_a_reply()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("pay").Disconnect();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("pay");

        Assert.Equal("", await client.ReadToEndAsync());
    }

    [Fact]
    public async Task Reply_then_disconnect()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("QUIT").Receive("BYE").AndDisconnect();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("QUIT");

        Assert.Equal("BYE", await client.ReadToEndAsync());
    }

    [Fact]
    public async Task Fail_twice_then_succeed()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("pay").Disconnect().ThenDisconnect().Then("PAID");
        server.Start();

        var attempts = 0;
        string result = null;
        while (result == null && attempts < 5)
        {
            attempts++;
            using var client = await TcpTestClient.ConnectAsync(server.Port);
            var response = await client.SendAndReceiveAsync("pay");
            if (response != "") result = response;     // "" means the server hung up
        }

        Assert.Equal("PAID", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Slow_then_fast()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("data")
            .Receive("late").After(TimeSpan.FromMilliseconds(300))
            .Then("quick");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("late", await client.SendAndReceiveAsync("data"));
        Assert.Equal("quick", await client.SendAndReceiveAsync("data"));
    }

    [Fact]
    public async Task Responses_keep_their_order_on_a_connection()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
        server.Mock.Send("slow").Receive("1").After(TimeSpan.FromMilliseconds(300));
        server.Mock.Send("fast").Receive("2");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("slow\nfast\n");

        Assert.Equal("1\n2\n", (await client.ReceiveExactlyAsync(4)).GetString());
    }

    [Fact]
    public async Task Connection_reset()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("X").ResetConnection();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("X");

        // A reset is an error on the client; a clean close would read 0 bytes.
        await Assert.ThrowsAnyAsync<IOException>(() => client.ReceiveAsync());
        await server.Connections[0].WaitForCloseAsync();
        server.Should().HaveNoOpenConnections();
    }

    [Fact]
    public async Task Reset_from_the_test()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        var connection = await server.WaitForConnectionAsync();
        await connection.ResetAsync();

        await Assert.ThrowsAnyAsync<IOException>(() => client.ReceiveAsync());
        connection.Should().BeClosed();
    }

    [Fact]
    public async Task Truncated_response()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.LengthPrefix() });
        server.Mock.Send("X").Receive("HELLO WORLD").Truncated(5).AndDisconnect();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync(new byte[] { 0, 0, 0, 1, (byte)'X' });   // a length-prefixed "X"

        // The prefix announces 11 bytes, but only 1 follows it: 5 bytes of the 15 on the wire.
        Assert.Equal("\0\0\0\vH", await client.ReadToEndAsync());
    }

    [Fact]
    public async Task Corrupted_response()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("X").Receive("HELLO").Corrupted(bytes => { bytes[0] ^= 0xFF; return bytes; });
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("X");

        Assert.Equal(new byte[] { (byte)'H' ^ 0xFF, (byte)'E', (byte)'L', (byte)'L', (byte)'O' }, await client.ReceiveExactlyAsync(5));
    }

    [Fact]
    public async Task Refuse_and_accept_connections()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("ping").Receive("pong");
        server.Start();
        using var open = await TcpTestClient.ConnectAsync(server.Port);

        server.RefuseConnections();
        var refused = await Assert.ThrowsAsync<SocketException>(() => TcpTestClient.ConnectAsync(server.Port));
        Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
        Assert.Equal("pong", await open.SendAndReceiveAsync("ping"));   // open connections keep working

        server.AcceptConnections();
        using var later = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("pong", await later.SendAndReceiveAsync("ping"));
    }
}
