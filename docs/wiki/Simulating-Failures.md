# Simulating Failures

Real servers are slow, drop connections and stop answering. Rony.Net lets you test how your client handles each of these.

| Method | What the client sees |
|---|---|
| `.After(delay)` | The response arrives after `delay` |
| `.InChunks(size, delay)` / `.Throttled(bytesPerSecond)` | The response arrives piece by piece, slowly |
| `NoReply()` / `.ThenNoReply()` | Nothing; the connection stays open (a timeout) |
| `Disconnect()` / `.ThenDisconnect()` | The connection closes without a response |
| `.AndDisconnect()` | The response, then the connection closes |
| `ResetConnection()` / `.ThenResetConnection()` / `.AndResetConnection()` | The connection is reset (RST), not closed cleanly |
| `.Truncated(byteCount)` | Only the first `byteCount` bytes of the response |
| `.Corrupted(bytes => ...)` | The response with changed bytes |
| `server.RefuseConnections()` | New clients get "connection refused" |

`Disconnect()` and `.AndDisconnect()` only apply to TCP; for UDP, `Disconnect()` behaves like `NoReply()`.
The same goes for the reset, truncation, corruption and refusing methods below: on a listener that cannot do them
(UDP, or a [custom listener](Custom-Listeners#simulating-failures-in-a-custom-listener)) a reset closes the connection like
`Disconnect()`, `Truncated` and `Corrupted` send the response unmodified (both log a line saying so), and
`RefuseConnections()` throws `NotSupportedException`.

## Slow responses
```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("report").Receive("done").After(TimeSpan.FromMilliseconds(500));
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
var stopwatch = Stopwatch.StartNew();

Assert.Equal("done", await client.SendAndReceiveAsync("report"));
Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(450));
```

A delayed response doesn't hold up other clients. On the **same** connection, responses still arrive in request order:

```csharp
using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
server.Mock.Send("slow").Receive("1").After(TimeSpan.FromMilliseconds(300));
server.Mock.Send("fast").Receive("2");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("slow\nfast\n");

Assert.Equal("1\n2\n", (await client.ReceiveExactlyAsync(4)).GetString());
```

`server.Stop()` cancels delays that are still waiting.

### Chunked and throttled responses
A real server often sends a large response in pieces over a long time. `.InChunks(chunkSize, delay)` sends the previous
response, **as it goes on the wire** (after [framing](Connections-and-Framing) and any `Truncated`/`Corrupted`), in
pieces of `chunkSize` bytes (the last may be shorter) and waits `delay` between the pieces, not before the first and not
after the last. `.Throttled(bytesPerSecond)` does the same at about that rate: ten pieces a second, or one byte at a
time for rates under 10 bytes per second. The rate is approximate: each piece is `bytesPerSecond / 10` bytes rounded
down, so a rate that is not a multiple of 10 comes out a little lower.

```csharp
using var server = new MockServer(new TcpServer(0));
var body = new string('x', 64);
server.Mock.Send("GET").Receive(body).InChunks(16, TimeSpan.FromMilliseconds(50));
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("GET");

// Four pieces of 16 bytes, 50 ms apart; the client sees the complete response in the end.
Assert.Equal(body, (await client.ReceiveExactlyAsync(64)).GetString());
```

```csharp
using var server = new MockServer(new TcpServer(0));
var body = new string('x', 300);
server.Mock.Send("GET").Receive(body).Throttled(bytesPerSecond: 1024);
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
var stopwatch = Stopwatch.StartNew();
await client.SendAsync("GET");

// About 1024 bytes a second: three pieces of 102 bytes, 100 ms apart.
Assert.Equal(body, (await client.ReceiveExactlyAsync(300)).GetString());
Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150));
```

- Every piece is written and flushed on its own. With a zero `delay` the client may still read several pieces at once.
- `.After(delay)` still delays the start; `.AndDisconnect()` and `.AndResetConnection()` happen after the last piece.
- A message pushed with `connection.SendAsync(...)` or `server.BroadcastAsync(...)` while a response is being sent waits
  until the last piece is written, so it never lands in the middle of the response.
- `server.Stop()` ends a slow response at once; if the client disconnects part-way, the failure is logged like any
  failed response.
- A connection used for a chunked response keeps `NoDelay` on afterwards, so small pieces are not held back.
- Calling `InChunks` and `Throttled` on the same step (or one of them twice): the last call wins.
- They work for `OnConnect()` greetings and `OnUnmatched()`, throw `ArgumentOutOfRangeException` for a chunk size or
  rate that is not positive (or a negative delay), and `InvalidOperationException` after `Disconnect()`, `NoReply()` or
  `ResetConnection()`. A listener that cannot send in chunks (UDP, or a
  [custom listener](Custom-Listeners#simulating-failures-in-a-custom-listener) without `IFaultInjectionListener`) sends
  the response whole and logs a line saying so. The log shows what happened, for example
  `#1 sent 64 bytes in 4 chunks of 16 bytes, 50 ms apart ...`.

## Timeouts: a server that never answers
```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("report").NoReply();
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("report");

// TcpTestClient gives up after 5 seconds; your client's own timeout is what you would test here.
await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReceiveAsync());
```

After a `NoReply()`, the connection stays usable: later requests on it are still answered.

## Dropped connections
```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("pay").Disconnect();
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("pay");

Assert.Equal("", await client.ReadToEndAsync());   // closed, nothing received
```

## Reply, then hang up
For protocols where the server closes the connection after a goodbye:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("QUIT").Receive("BYE").AndDisconnect();
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("QUIT");

Assert.Equal("BYE", await client.ReadToEndAsync());
```

## Connection reset
`Disconnect()` ends the stream cleanly: the client reads 0 bytes. A real network failure often looks different, with a
TCP reset (RST) that makes the client's read throw. `ResetConnection()` aborts the connection that way:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("X").ResetConnection();
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("X");

// A reset is an error on the client; a clean close would read 0 bytes.
await Assert.ThrowsAnyAsync<IOException>(() => client.ReceiveAsync());
await server.Connections[0].WaitForCloseAsync();
server.Should().HaveNoOpenConnections();
```

A Unix domain socket ([`UnixSocketServer`](Servers#unix-domain-sockets)) has no RST: there `ResetConnection()` and
`connection.ResetAsync()` close the connection abruptly, and the client sees the end of the stream or a reset error,
depending on the platform.

For a `UnixSocketServer`, `RefuseConnections()` removes the socket file, so a refused client fails with a platform-dependent
`SocketException` (not necessarily `ConnectionRefused`).

`.ThenResetConnection()` does the same inside a [sequence](Response-Sequences), and `.AndResetConnection()` resets right
after a response was written. **A reset discards data that has not been delivered yet, so the client may not see that
response.** To reset at a moment of your choosing, call `await connection.ResetAsync()`:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
var connection = await server.WaitForConnectionAsync();
await connection.ResetAsync();

await Assert.ThrowsAnyAsync<IOException>(() => client.ReceiveAsync());
connection.Should().BeClosed();
```

`ConnectionClosed` is raised once, as for a normal close. With TLS the socket is aborted without a `close_notify`.

## Truncated and corrupted responses
`.Truncated(byteCount)` sends only the first `byteCount` bytes of the previous response, **as it goes on the wire**: after
[framing](Connections-and-Framing) is applied. With a length prefix, the client sees a message that announces more than
arrives:

```csharp
using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.LengthPrefix() });
server.Mock.Send("X").Receive("HELLO WORLD").Truncated(5).AndDisconnect();
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync(new byte[] { 0, 0, 0, 1, (byte)'X' });   // a length-prefixed "X"

// The prefix announces 11 bytes, but only 1 follows it: 5 bytes of the 15 on the wire.
Assert.Equal("\0\0\0\vH", await client.ReadToEndAsync());
```

A `byteCount` of at least the length sends everything and `0` sends nothing. Truncating does not close the connection;
add `.AndDisconnect()` (or `.AndResetConnection()`) for that, otherwise the client waits for the rest.

`.Corrupted(func)` changes the bytes. The function gets a copy of the framed bytes (the configured response is never
modified) and returns the bytes to send; `null` means nothing. If it throws, the error is logged and the response is
sent unmodified.

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("X").Receive("HELLO").Corrupted(bytes => { bytes[0] ^= 0xFF; return bytes; });
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("X");

Assert.Equal(new byte[] { (byte)'H' ^ 0xFF, (byte)'E', (byte)'L', (byte)'L', (byte)'O' }, await client.ReceiveExactlyAsync(5));
```

Both modify the previous response and apply in the order you call them. They also work for `OnConnect()` greetings and
`OnUnmatched()`. They throw `InvalidOperationException` after `Disconnect()`, `NoReply()` or `ResetConnection()`, which
send nothing. The [log](Logging-and-Diagnostics) shows what was really sent, for example
`#1 sent 5 of 15 bytes (truncated) ...`.

## Refusing connections
`server.RefuseConnections()` closes the listening socket: a client that tries to connect gets "connection refused".
Connections the server has already accepted keep working. The server first accepts the clients that are already waiting, so only a client whose connect completes at that very moment may be reset instead of refused; wait with `WaitForConnectionAsync()` for the clients you expect before refusing. `server.AcceptConnections()` listens again on the same port. While connections are refused the port is free, so another process could take it; then `AcceptConnections()` throws a `SocketException`.

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("ping").Receive("pong");
server.Start();
using var open = await TcpTestClient.ConnectAsync(server.Port);
await server.WaitForConnectionAsync();   // the server has accepted it, so it stays open

server.RefuseConnections();
var refused = await Assert.ThrowsAsync<SocketException>(() => TcpTestClient.ConnectAsync(server.Port));
Assert.Equal(SocketError.ConnectionRefused, refused.SocketErrorCode);
Assert.Equal("pong", await open.SendAndReceiveAsync("ping"));   // open connections keep working

server.AcceptConnections();
using var later = await TcpTestClient.ConnectAsync(server.Port);
Assert.Equal("pong", await later.SendAndReceiveAsync("ping"));
```

`Active` and `Port` don't change while refusing. Both methods do nothing when repeated. `RefuseConnections()` throws
`InvalidOperationException` before `Start()` or after `Stop()`, and `Stop()` ends the refusing, so the next `Start()` listens
normally. To make a TLS handshake fail instead, see [SSL and TLS](SSL-and-TLS#failing-the-handshake).

## A flaky server
Combine failures with a [sequence](Response-Sequences) to test retry logic. This server drops the first two attempts and then answers:

```csharp
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
```

Other combinations:
```csharp
server.Mock.Send("data").NoReply().Then("ok");                             // time out once, then answer
server.Mock.Send("data").Receive("ok").After(TimeSpan.FromSeconds(10))     // slow first time...
                        .Then("ok");                                       // ...fast afterwards
server.Mock.Send("data").Receive("ok").Then("ok").ThenDisconnect();        // works twice, then breaks
```

For a complete example with a real client class, retries and timeouts, see [Recipes](Recipes#test-a-client-class).

## Other failures
- **Server not running:** call `server.Stop()`. Connections are refused, and existing ones are closed. To refuse only new
  clients, use `RefuseConnections()` (see above).
- **Connections dropped on arrival:** `server.Mock.OnConnect().Disconnect()`, or turn away only later clients with
  `OnConnect().Receive("200 welcome").Then("421 busy").AndDisconnect()`. See [Connections and Push](Connections-and-Push#greetings-talk-first).
- **The server hangs up on its own:** `await server.Connections[0].CloseAsync()`, at any moment in the test.
- **Server comes back:** call `server.Start()` again. With port `0`, it returns on the same port.
- **Garbage or error responses:** configure them like any other response, for example `Receive(new byte[] { 0xFF, 0xFF })` or `Receive("ERR 500")`.

Runnable code: [`SequenceAndFailureSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/SequenceAndFailureSamples.cs)
