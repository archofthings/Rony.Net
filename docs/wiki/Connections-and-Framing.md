# Connections and Framing

This page applies to `TcpServer` and `TcpServerSsl`. With UDP, every datagram is already one message.

## Persistent connections
A client can send any number of requests over one connection:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("ping").Receive("pong");
server.Mock.Send("pong").Receive("ping");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);

Assert.Equal("pong", await client.SendAndReceiveAsync("ping"));
Assert.Equal("ping", await client.SendAndReceiveAsync("pong"));
Assert.Equal("pong", await client.SendAndReceiveAsync("ping"));
```

How connections are handled:
- **Independently.** A slow or silent client never blocks the others.
- **In order.** Responses on one connection are sent in the order of their requests, even when some are delayed.
- **Closed when needed.** A connection closes when the client closes it, when the server stops, after
  [`Disconnect()` or `AndDisconnect()`](Simulating-Failures), or when a request has no matching response.
  If the client stops sending (half-close), it still gets the responses to the requests it already sent.

## One response per connection
Some clients send a request and then read until the server closes the connection. For those, set `KeepAlive = false`:

```csharp
using var server = new MockServer(new TcpServer(0) { KeepAlive = false });
server.Mock.Send("GET").Receive("data");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("GET");

// The client can read until the server closes the connection.
Assert.Equal("data", await client.ReadToEndAsync());
```

To close after only some responses, use `.AndDisconnect()` on those responses instead; see [Simulating Failures](Simulating-Failures).

## Framing: where does a message end?
TCP is a stream of bytes. It doesn't keep message boundaries: two messages can arrive together, and one message can
arrive in several pieces. *Framing* tells the server how to split the stream back into messages.

| Framing | A request is | A response is sent as |
|---|---|---|
| `MessageFraming.None` (default) | Whatever arrives in one burst | It is |
| `MessageFraming.Delimiter("\n")` | The bytes up to the delimiter (the delimiter is removed) | The response + the delimiter |
| `MessageFraming.LengthPrefix(2)` | A length, then that many bytes (the prefix is removed) | A length prefix + the response |
| `MessageFraming.LengthPrefix(2, bigEndian: true, includesPrefix: true)` | Like `LengthPrefix`, but the length counts the prefix too | Same, with the prefix counted in the length |
| `MessageFraming.FixedLength(8)` | Every 8 bytes (nothing is removed) | The response, padded on the right to 8 bytes |
| `MessageFraming.StxEtx` / `MessageFraming.StartEnd(start, end)` | The bytes between a start and an end byte (both removed) | Start byte + the response + end byte |
| Your own `IMessageFraming` | Whatever you decide | Whatever you decide |

The default works when the client sends one request and waits for the response before sending the next.
Use framing when your protocol defines message boundaries, or when your client sends several requests without waiting.

Requests are matched **after** framing is removed, so you configure `Send("QUIT")`, not `Send("QUIT\r\n")`.

### Delimited messages (line-based protocols)
```csharp
using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\r\n") });
server.Mock.Send("HELO client").Receive("250 Hello");
server.Mock.Send("QUIT").Receive("221 Bye").AndDisconnect();
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);

// Two commands in one packet are still two requests; responses get "\r\n" appended.
await client.SendAsync("HELO client\r\nQUIT\r\n");

Assert.Equal("250 Hello\r\n221 Bye\r\n", await client.ReadToEndAsync());
```

A request split over several packets is put back together:
```csharp
using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
server.Mock.Send("hello world").Receive("hi");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("hello ");
await Task.Delay(50);
await client.SendAsync("world\n");

Assert.Equal("hi\n", await client.ReceiveAsync());
```

The delimiter can be any text or bytes: `MessageFraming.Delimiter(new byte[] { 0x00 })`.
Don't include the delimiter in your configured responses, because it is appended for you.

### Length-prefixed messages (binary protocols)
`MessageFraming.LengthPrefix(prefixLength, bigEndian)` reads a 1, 2 or 4-byte unsigned length before each message.
The default is a 4-byte big-endian length.

```csharp
var framing = MessageFraming.LengthPrefix(prefixLength: 2, bigEndian: true);
using var server = new MockServer(new TcpServer(0) { Framing = framing });
server.Mock.Send(new byte[] { 0x01, 0x02 }).Receive(new byte[] { 0xAA, 0xBB, 0xCC });
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync(new byte[] { 0x00, 0x02, 0x01, 0x02 });   // length 2, then the payload

var response = await client.ReceiveExactlyAsync(5);
Assert.Equal(new byte[] { 0x00, 0x03, 0xAA, 0xBB, 0xCC }, response);
```

`framing.Encode(bytes)` adds the prefix for you, which is handy when building requests in a test.

For a little-endian length, pass `bigEndian: false`:
```csharp
using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.LengthPrefix(4, bigEndian: false) });
server.Mock.Send(new byte[] { 0x01 }).Receive(new byte[] { 0xAA, 0xBB });
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x01 });   // length 1 (little-endian), then the payload

var response = await client.ReceiveExactlyAsync(6);
Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 0xAA, 0xBB }, response);
```

### Lengths that include the prefix
Some protocols count the prefix in the length. With `includesPrefix: true` the length is the size of the whole frame,
so a 3-byte payload behind a 2-byte prefix has the length 5:

```csharp
var framing = MessageFraming.LengthPrefix(2, bigEndian: true, includesPrefix: true);
using var server = new MockServer(new TcpServer(0) { Framing = framing });
server.Mock.Send(new byte[] { 0x01, 0x02, 0x03 }).Receive(new byte[] { 0xAA });
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync(new byte[] { 0x00, 0x05, 0x01, 0x02, 0x03 });   // length 5 = 2 prefix bytes + 3 payload bytes

var response = await client.ReceiveExactlyAsync(3);
Assert.Equal(new byte[] { 0x00, 0x03, 0xAA }, response);
```

A length smaller than the prefix itself can never be valid. The server closes that one connection and reports it
through `ConnectionFailed` and the log; other connections and the server keep running. A response that doesn't fit
in the prefix throws `InvalidOperationException`.

When a length is invalid, messages decoded earlier in the same burst are dropped together with the connection.

### Fixed-length messages
`MessageFraming.FixedLength(length, padding)` treats every `length` bytes as one message. Requests are delivered
as they are, padding included, so configure the padded text. A shorter response is padded on the right with
`padding` (default 0); a longer one throws `InvalidOperationException`.

```csharp
using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.FixedLength(8, padding: (byte)' ') });
server.Mock.Send("PING    ").Receive("PONG");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("PING    ");

Assert.Equal("PONG    ", await client.ReceiveAsync());
```

### Start and end bytes
`MessageFraming.StartEnd(start, end)` wraps messages in two marker bytes, and `MessageFraming.StxEtx` is the
common case 0x02 ... 0x03. The markers are removed from requests and added to responses; bytes outside a message
are ignored, and a message split over several packets is put back together. There is no escaping and no checksum, so
an end byte inside the payload ends the message. The start and end byte must differ.

```csharp
using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.StxEtx });
server.Mock.Send("STATUS").Receive("OK");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync(new byte[] { 0x02 }.Concat("STATUS".GetBytes()).Append((byte)0x03).ToArray());

var response = await client.ReceiveBytesAsync();
Assert.Equal(new byte[] { 0x02, (byte)'O', (byte)'K', 0x03 }, response);
```

### Custom framing
Implement `IMessageFraming` for any other format. This one handles messages wrapped in STX (0x02) and ETX (0x03), like the built-in `StxEtx`, to show how it's done:

```csharp
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
```

```csharp
using var server = new MockServer(new TcpServer(0) { Framing = new StxEtxFraming() });
server.Mock.Send("STATUS").Receive("OK");
```

How `Decode` is called:
- `data` holds every byte received on the connection that isn't part of a message yet.
- Return each complete message at the start of `data`, and set `consumed` to the number of bytes they used.
  Bytes you don't consume are kept and passed in again, with more data, on the next call.
- `endOfBurst` is `true` when no more data is waiting on the connection right now. `MessageFraming.None` uses it to
  treat a whole burst as one message.

`Encode` is called for every non-empty response. An empty response sends nothing, with any framing.

Runnable code: [`ConnectionAndFramingSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ConnectionAndFramingSamples.cs)
