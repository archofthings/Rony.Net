using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Verifying-Requests, Waiting-for-Requests
public class VerificationSamples
{
    [Fact]
    public async Task Verify_what_the_client_sent()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("LOGIN alice").Receive("OK");
        server.Mock.Send("LIST").Receive("a,b,c");
        server.Start();

        using (var client = await TcpTestClient.ConnectAsync(server.Port))
        {
            await client.SendAndReceiveAsync("LOGIN alice");
            await client.SendAndReceiveAsync("LIST");
            await client.SendAndReceiveAsync("LIST");
        }

        server.Should().HaveReceived("LOGIN alice");                       // at least once
        server.Should().HaveReceived("LIST", Times.Exactly(2));
        server.Should().NotHaveReceived("LOGOUT");
        server.Should().HaveReceived(r => r.BodyString.StartsWith("LOGIN"), Times.Once());
    }

    [Fact]
    public async Task Verify_what_one_connection_sent()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("").Receive("ok");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAndReceiveAsync("LOGIN bob");
        await client.SendAndReceiveAsync("LIST");
        using var other = await TcpTestClient.ConnectAsync(server.Port);
        await other.SendAndReceiveAsync("QUIT");

        var connection = server.Connections[0];
        connection.Should().HaveReceived("LOGIN bob", Times.Once())
            .And.HaveReceivedInOrder("LOGIN bob", "LIST")
            .And.NotHaveReceived("QUIT");
        server.Should().HaveReceived("QUIT");   // the server-wide check still sees every connection
        connection.Should().BeOpen();
    }

    [Fact]
    public void All_the_Times_options()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Match("x");
        server.Mock.Match("x");

        server.Should().HaveReceived("x", Times.Exactly(2));
        server.Should().HaveReceived("x", Times.AtLeast(1));
        server.Should().HaveReceived("x", Times.AtMost(3));
        server.Should().HaveReceived("x", Times.Between(1, 2));
        server.Should().HaveReceived("x", Times.AtLeastOnce());
        server.Should().NotHaveReceived("y");
    }

    [Fact]
    public void A_failed_verification_explains_itself()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("PING").Receive("PONG");
        server.Mock.Match("PING");
        server.Mock.Match("PNIG");

        var error = Assert.Throws<MockVerificationException>(() => server.Should().HaveReceived("PING", Times.Exactly(2)));

        // Expected request "PING" exactly 2 times, but it was received 1 time.
        // Received requests:
        //   1. "PING"
        //   2. "PNIG" (unmatched)
        Assert.Contains("\"PNIG\" (unmatched)", error.Message);
    }

    [Fact]
    public async Task Strict_mode()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("PING").Receive("PONG");
        server.Start();

        using (var client = await TcpTestClient.ConnectAsync(server.Port))
            await client.SendAndReceiveAsync("PING");

        // Passes: every request had a configured response.
        server.Should().HaveNoUnmatchedRequests();
    }

    [Fact]
    public async Task Inspect_received_requests()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("").Receive("ok");
        server.Start();

        using (var client = await TcpTestClient.ConnectAsync(server.Port))
        {
            await client.SendAndReceiveAsync("first");
            await client.SendAndReceiveAsync("second");
        }

        IReadOnlyList<ReceivedRequest> requests = server.ReceivedRequests;
        Assert.Equal(new[] { "first", "second" }, requests.Select(r => r.BodyString));
        Assert.All(requests, r => Assert.True(r.Matched));
        Assert.All(requests, r => Assert.NotNull(r.RemoteEndPoint));
        Assert.True(requests[0].Timestamp <= requests[1].Timestamp);
    }

    [Fact]
    public async Task Wait_for_a_fire_and_forget_message()
    {
        using var server = new MockServer(new UdpServer("127.0.0.1", 0));
        server.Mock.Send("").NoReply();
        server.Start();

        // Imagine this is your code sending a heartbeat in the background.
        _ = Task.Run(async () =>
        {
            using var udp = new System.Net.Sockets.UdpClient();
            var data = "HEARTBEAT".GetBytes();
            await udp.SendAsync(data, data.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, server.Port));
        });

        var request = await server.Mock.WaitForRequestAsync("HEARTBEAT", TimeSpan.FromSeconds(5));

        Assert.Equal("HEARTBEAT", request.BodyString);
    }

    [Fact]
    public async Task Wait_for_several_requests()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("").Receive("ok");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        foreach (var command in new[] { "a", "b", "c" })
            await client.SendAndReceiveAsync(command);

        var requests = await server.Mock.WaitForRequestsAsync(count: 3);
        var second = await server.Mock.WaitForRequestAsync(r => r.BodyString == "b");

        Assert.Equal(3, requests.Count);
        Assert.Equal("b", second.BodyString);
    }

    [Fact]
    public async Task Waiting_times_out_with_details()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Match("something else");

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => server.Mock.WaitForRequestAsync("expected", TimeSpan.FromMilliseconds(200)));

        Assert.Contains("\"something else\"", error.Message);
    }

    [Fact]
    public void Start_over_between_steps()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("ping").Receive("pong");
        server.Mock.Match("ping");

        server.Mock.ClearReceivedRequests();

        server.Should().NotHaveReceived("ping");
        Assert.Single(server.Mock.Configs);   // responses are kept
    }

    [Fact]
    public async Task Verify_the_order()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("").Receive("OK");
        server.Start();

        using (var client = await TcpTestClient.ConnectAsync(server.Port))
        {
            foreach (var command in new[] { "LOGIN bob", "NOOP", "LIST", "QUIT" })
                await client.SendAndReceiveAsync(command);
        }

        server.Should().HaveReceivedInOrder("LOGIN bob", "LIST", "QUIT");   // NOOP in between is fine
        server.Should().HaveReceivedInOrder(r => r.BodyString.StartsWith("LOGIN"), r => r.BodyString == "QUIT");
    }

    [Fact]
    public void A_wrong_order_explains_itself()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Match("LIST");
        server.Mock.Match("LOGIN bob");

        var error = Assert.Throws<MockVerificationException>(() => server.Should().HaveReceivedInOrder("LOGIN bob", "LIST"));

        // Expected requests in order: "LOGIN bob", "LIST", but "LIST" was not received after "LOGIN bob".
        // Received requests:
        //   1. "LIST" (unmatched)
        //   2. "LOGIN bob" (unmatched)
        Assert.Contains("\"LIST\" was not received after \"LOGIN bob\"", error.Message);
    }

    [Fact]
    public async Task Fluent_assertions()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("LOGIN bob").Receive("OK");
        server.Mock.Send("LIST").Receive("a,b,c");
        server.Start();

        using (var client = await TcpTestClient.ConnectAsync(server.Port))
        {
            await client.SendAndReceiveAsync("LOGIN bob");
            await client.SendAndReceiveAsync("LIST");
        }

        server.Should().HaveReceived("LOGIN bob", Times.Once())
            .And.HaveReceived("LIST")
            .And.NotHaveReceived("DELETE")
            .And.HaveReceivedInOrder("LOGIN bob", "LIST")
            .And.HaveNoUnmatchedRequests()
            .And.HaveAcceptedConnections(Times.Once());
    }

    [Fact]
    public async Task Limit_and_journal_the_received_requests()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("").Receive("ok");
        var output = new StringWriter();
        var journal = TextWriter.Synchronized(output);

        server.Mock.MaxReceivedRequests = 2;
        server.RequestReceived += (sender, request) => journal.WriteLine(request.ToJson());
        server.Start();

        using (var client = await TcpTestClient.ConnectAsync(server.Port))
        {
            await client.SendAndReceiveAsync("one");
            await client.SendAndReceiveAsync("two");
            await client.SendAndReceiveAsync("three");
        }

        Assert.Equal(new[] { "two", "three" }, server.ReceivedRequests.Select(r => r.BodyString));
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"text\":\"one\"", lines[0]);
    }
}
