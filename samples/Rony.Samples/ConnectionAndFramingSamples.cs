using Rony;
using Rony.Interfaces;
using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Connections-and-Framing
public class ConnectionAndFramingSamples
{
    [Fact]
    public async Task Many_requests_on_one_connection()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("ping").Receive("pong");
        server.Mock.Send("pong").Receive("ping");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);

        Assert.Equal("pong", await client.SendAndReceiveAsync("ping"));
        Assert.Equal("ping", await client.SendAndReceiveAsync("pong"));
        Assert.Equal("pong", await client.SendAndReceiveAsync("ping"));
    }

    [Fact]
    public async Task Close_after_every_response()
    {
        using var server = new MockServer(new TcpServer(0) { KeepAlive = false });
        server.Mock.Send("GET").Receive("data");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("GET");

        // The client can read until the server closes the connection.
        Assert.Equal("data", await client.ReadToEndAsync());
    }

    [Fact]
    public async Task Line_based_protocol_with_a_delimiter()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\r\n") });
        server.Mock.Send("HELO client").Receive("250 Hello");
        server.Mock.Send("QUIT").Receive("221 Bye").AndDisconnect();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);

        // Two commands in one packet are still two requests; responses get "\r\n" appended.
        await client.SendAsync("HELO client\r\nQUIT\r\n");

        Assert.Equal("250 Hello\r\n221 Bye\r\n", await client.ReadToEndAsync());
    }

    [Fact]
    public async Task Request_split_over_several_packets()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
        server.Mock.Send("hello world").Receive("hi");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("hello ");
        await client.SendAsync("world\n");

        Assert.Equal("hi\n", await client.ReceiveAsync());
    }

    [Fact]
    public async Task Binary_protocol_with_a_length_prefix()
    {
        var framing = MessageFraming.LengthPrefix(prefixLength: 2, bigEndian: true);
        using var server = new MockServer(new TcpServer(0) { Framing = framing });
        server.Mock.Send(new byte[] { 0x01, 0x02 }).Receive(new byte[] { 0xAA, 0xBB, 0xCC });
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync(new byte[] { 0x00, 0x02, 0x01, 0x02 });   // length 2, then the payload

        var response = await client.ReceiveExactlyAsync(5);
        Assert.Equal(new byte[] { 0x00, 0x03, 0xAA, 0xBB, 0xCC }, response);
    }

    [Fact]
    public async Task Little_endian_length_prefix()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.LengthPrefix(4, bigEndian: false) });
        server.Mock.Send(new byte[] { 0x01 }).Receive(new byte[] { 0xAA, 0xBB });
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x01 });   // length 1 (little-endian), then the payload

        var response = await client.ReceiveExactlyAsync(6);
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 0xAA, 0xBB }, response);
    }

    [Fact]
    public async Task Length_that_includes_the_prefix()
    {
        var framing = MessageFraming.LengthPrefix(2, bigEndian: true, includesPrefix: true);
        using var server = new MockServer(new TcpServer(0) { Framing = framing });
        server.Mock.Send(new byte[] { 0x01, 0x02, 0x03 }).Receive(new byte[] { 0xAA });
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync(new byte[] { 0x00, 0x05, 0x01, 0x02, 0x03 });   // length 5 = 2 prefix bytes + 3 payload bytes

        var response = await client.ReceiveExactlyAsync(3);
        Assert.Equal(new byte[] { 0x00, 0x03, 0xAA }, response);
    }

    [Fact]
    public void Limiting_the_buffered_bytes()
    {
        var tcpServer = new TcpServer(0) { Framing = MessageFraming.Delimiter("\n"), MaxBufferedBytes = 64 * 1024 };

        Assert.Equal(64 * 1024, tcpServer.MaxBufferedBytes);
    }

    [Fact]
    public async Task Fixed_length_messages()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.FixedLength(8, padding: (byte)' ') });
        server.Mock.Send("PING    ").Receive("PONG");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("PING    ");

        Assert.Equal("PONG    ", await client.ReceiveAsync());
    }

    [Fact]
    public async Task Start_and_end_bytes()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.StxEtx });
        server.Mock.Send("STATUS").Receive("OK");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync(new byte[] { 0x02 }.Concat("STATUS".GetBytes()).Append((byte)0x03).ToArray());

        var response = await client.ReceiveBytesAsync();
        Assert.Equal(new byte[] { 0x02, (byte)'O', (byte)'K', 0x03 }, response);
    }

    [Fact]
    public async Task Custom_framing_with_start_and_end_markers()
    {
        using var server = new MockServer(new TcpServer(0) { Framing = new StxEtxFraming() });
        server.Mock.Send("STATUS").Receive("OK");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync(new byte[] { 0x02 }.Concat("STATUS".GetBytes()).Append((byte)0x03).ToArray());

        var response = await client.ReceiveBytesAsync();
        Assert.Equal(new byte[] { 0x02, (byte)'O', (byte)'K', 0x03 }, response);
    }
}

/// <summary>
/// Messages look like STX (0x02) + payload + ETX (0x03). Bytes outside a frame are skipped.
/// </summary>
public sealed class StxEtxFraming : IMessageFraming
{
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;

    public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)
    {
        var messages = new List<byte[]>();
        consumed = 0;
        while (true)
        {
            var start = data[consumed..].IndexOf(Stx);
            if (start < 0) { consumed = data.Length; break; }          // no frame start: drop the noise
            var end = data[(consumed + start)..].IndexOf(Etx);
            if (end < 0) { consumed += start; break; }                 // incomplete frame: wait for more data
            messages.Add(data.Slice(consumed + start + 1, end - 1).ToArray());
            consumed += start + end + 1;
        }
        return messages;
    }

    public byte[] Encode(byte[] response) => new[] { Stx }.Concat(response).Append(Etx).ToArray();
}
