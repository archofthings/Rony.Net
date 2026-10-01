using System.Text.RegularExpressions;
using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Configuring-Responses, Request-Matching
public class ResponseSamples
{
    [Fact]
    public async Task Text_and_byte_responses()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("version").Receive("1.0.0");
        server.Mock.Send(new byte[] { 0x01, 0x02 }).Receive(new byte[] { 0x03, 0x04 });
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("1.0.0", await client.SendAndReceiveAsync("version"));

        await client.SendAsync(new byte[] { 0x01, 0x02 });
        Assert.Equal(new byte[] { 0x03, 0x04 }, await client.ReceiveBytesAsync());
    }

    [Fact]
    public async Task Response_computed_from_the_request()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("hello").Receive(text => text.ToUpper());
        server.Mock.Send(new byte[] { 1, 2, 3 }).Receive(bytes => bytes.Reverse().ToArray());
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("HELLO", await client.SendAndReceiveAsync("hello"));

        await client.SendAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 3, 2, 1 }, await client.ReceiveBytesAsync());
    }

    [Fact]
    public async Task Echo_server()
    {
        using var server = new MockServer(new TcpServer(0));
        // An identity lambda needs its type spelled out: both Receive(Func<string, string>)
        // and Receive(Func<byte[], byte[]>) would accept "x => x".
        server.Mock.Send("").Receive((byte[] request) => request);
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("anything at all", await client.SendAndReceiveAsync("anything at all"));
    }

    [Fact]
    public async Task Default_response_for_any_request()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("known").Receive("specific answer");
        server.Mock.Send("").Receive("ERROR unknown command");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("specific answer", await client.SendAndReceiveAsync("known"));
        Assert.Equal("ERROR unknown command", await client.SendAndReceiveAsync("something else"));
    }

    [Fact]
    public async Task Unmatched_request_closes_the_connection()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("known").Receive("yes");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("unknown");

        Assert.Equal("", await client.ReadToEndAsync());
    }

    [Fact]
    public async Task Regex_matching()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send(new Regex(@"^LOGIN \w+ \w+$")).Receive("OK");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("OK", await client.SendAndReceiveAsync("LOGIN alice secret"));
    }

    [Fact]
    public async Task Predicate_matching_on_text_and_bytes()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.SendMatching(text => text.StartsWith("GET ")).Receive("200 OK");
        server.Mock.SendMatchingBytes(bytes => bytes.Length > 0 && bytes[0] == 0xFF).Receive(new byte[] { 0x00 });
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("200 OK", await client.SendAndReceiveAsync("GET /orders/42"));

        await client.SendAsync(new byte[] { 0xFF, 0x10 });
        Assert.Equal(new byte[] { 0x00 }, await client.ReceiveBytesAsync());
    }

    [Fact]
    public async Task Request_with_a_changing_id()
    {
        // The client sends "ORDER <id>" and expects "ACK <id>" back.
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send(new Regex(@"^ORDER \d+$")).Receive(request => "ACK " + request.Split(' ')[1]);
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("ACK 1001", await client.SendAndReceiveAsync("ORDER 1001"));
        Assert.Equal("ACK 1002", await client.SendAndReceiveAsync("ORDER 1002"));
    }

    [Fact]
    public void Matching_precedence()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("").Receive("any");                               // 3. fallback
        server.Mock.SendMatching(text => text.StartsWith("A")).Receive("A*"); // 2. patterns, in the order added
        server.Mock.Send("ABC").Receive("exact");                          // 1. exact request

        // Match(...) runs the same lookup the server does, without a network.
        Assert.Equal("exact", server.Mock.Match("ABC").GetString());
        Assert.Equal("A*", server.Mock.Match("AXY").GetString());
        Assert.Equal("any", server.Mock.Match("XYZ").GetString());
    }

    [Fact]
    public void Same_exact_request_twice_is_an_error()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("ping").Receive("pong");

        Assert.Throws<ArgumentException>(() => server.Mock.Send("ping").Receive("other"));
    }

    [Fact]
    public void Reset_removes_everything()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("ping").Receive("pong");
        server.Mock.Match("ping");

        server.Mock.Reset();

        Assert.Empty(server.Mock.Configs);
        Assert.Empty(server.Mock.ReceivedRequests);
    }
}
