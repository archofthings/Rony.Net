using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Request-Matching (Unmatched requests), Verifying-Requests (Fail fast)
public class UnmatchedRequestSamples
{
    [Fact]
    public async Task Default_unmatched_request_closes_the_connection()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("PING").Receive("PONG");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("PNIG");

        Assert.Equal("", await client.ReadToEndAsync());
    }

    [Fact]
    public async Task Answer_unmatched_requests_with_an_error()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("PING").Receive("PONG");
        server.Mock.OnUnmatched().Receive(text => $"ERR unknown command '{text}'");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("ERR unknown command 'PNIG'", await client.SendAndReceiveAsync("PNIG"));
        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));   // still connected

        Assert.Single(server.Mock.UnmatchedRequests);                     // still reported
        Assert.Throws<MockVerificationException>(() => server.Mock.VerifyAllRequestsMatched());
    }

    [Fact]
    public async Task Ignore_unmatched_requests()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("PING").Receive("PONG");
        server.Mock.OnUnmatched().NoReply();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("NOISE");
        await server.Mock.WaitForRequestAsync("NOISE");
        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));
    }

    [Fact]
    public async Task Fail_fast_on_an_unexpected_request()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.FailOnUnmatched = true;
        server.Mock.Send("HEARTBEAT").NoReply();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("HEARTBAET");   // the bug under test

        // Fails as soon as the typo arrives, instead of after 30 seconds.
        var error = await Assert.ThrowsAsync<MockVerificationException>(
            () => server.Mock.WaitForRequestAsync("HEARTBEAT", TimeSpan.FromSeconds(30)));
        Assert.Contains("\"HEARTBAET\"", error.Message);
    }
}
