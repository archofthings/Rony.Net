using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Connections-and-Push
public class ConnectionAndPushSamples
{
    [Fact]
    public async Task Greet_every_client()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.OnConnect().Receive("220 mail.test ESMTP ready\r\n");
        server.Mock.Send("QUIT\r\n").Receive("221 bye\r\n").AndDisconnect();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("220 mail.test ESMTP ready\r\n", await client.ReceiveAsync());   // before sending anything
        Assert.Equal("221 bye\r\n", await client.SendAndReceiveAsync("QUIT\r\n"));
    }

    [Fact]
    public async Task A_different_greeting_per_connection()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.OnConnect()
            .Receive("200 welcome")
            .Then("421 too many connections").AndDisconnect();
        server.Start();

        using var first = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("200 welcome", await first.ReceiveAsync());

        using var second = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("421 too many connections", await second.ReadToEndAsync());
    }

    [Fact]
    public async Task Refuse_connections()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.OnConnect().Disconnect();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("", await client.ReadToEndAsync());
    }

    [Fact]
    public async Task Inspect_connections()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("").Receive("ok");
        server.Start();

        using (var client = await TcpTestClient.ConnectAsync(server.Port))
        {
            await client.SendAndReceiveAsync("first");
            await client.SendAndReceiveAsync("second");
        }

        ClientConnection connection = server.Connections.Single();
        Assert.Equal(1, connection.Id);
        Assert.NotNull(connection.RemoteEndPoint);
        Assert.Equal(new[] { "first", "second" }, connection.ReceivedRequests.Select(r => r.BodyString));
        Assert.Equal(connection.Id, server.ReceivedRequests[0].ConnectionId);
    }

    [Fact]
    public async Task Check_that_the_client_reuses_its_connection()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("").Receive("ok");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAndReceiveAsync("one");
        await client.SendAndReceiveAsync("two");

        server.Should().HaveAcceptedConnections(Times.Once());
    }

    [Fact]
    public async Task Check_that_the_client_closes_its_connection()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("BYE").Receive("ok");
        server.Start();

        var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAndReceiveAsync("BYE");
        client.Dispose();   // the code under test should do this

        var connection = await server.WaitForConnectionAsync();
        await connection.WaitForCloseAsync();
        Assert.False(connection.IsOpen);
        Assert.Empty(server.OpenConnections);
    }

    [Fact]
    public async Task Connection_events()
    {
        using var server = new MockServer(new TcpServer(0));
        var opened = new List<int>();
        server.ConnectionOpened += (_, connection) => { lock (opened) opened.Add(connection.Id); };
        server.ConnectionClosed += (_, connection) => { /* connection.ClosedAt is set */ };
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await server.WaitForConnectionAsync();

        lock (opened) Assert.Equal(new[] { 1 }, opened);
    }

    [Fact]
    public async Task Push_a_message_to_one_client()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        var connection = await server.WaitForConnectionAsync();

        await connection.SendAsync("NOTIFY price-changed");

        Assert.Equal("NOTIFY price-changed", await client.ReceiveAsync());
    }

    [Fact]
    public async Task Broadcast_to_every_client()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
        server.Start();

        using var alice = await TcpTestClient.ConnectAsync(server.Port);
        using var bob = await TcpTestClient.ConnectAsync(server.Port);
        await server.WaitForConnectionsAsync(2);

        var sent = await server.BroadcastAsync("SHUTDOWN in 5 minutes");

        Assert.Equal(2, sent);
        Assert.Equal("SHUTDOWN in 5 minutes\n", await alice.ReceiveAsync());   // framed like a response
        Assert.Equal("SHUTDOWN in 5 minutes\n", await bob.ReceiveAsync());
    }

    [Fact]
    public async Task Close_a_connection_from_the_server()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        var connection = await server.WaitForConnectionAsync();

        await connection.CloseAsync();

        Assert.Equal("", await client.ReadToEndAsync());
    }
}
