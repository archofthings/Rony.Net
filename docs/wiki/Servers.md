# Servers

A `MockServer` runs on a *listener*, which is the transport. Rony.Net includes four:

| Listener | Protocol | Default address | Notes |
|---|---|---|---|
| `TcpServer` | TCP | `127.0.0.1` | Connections stay open across requests |
| `TcpServerSsl` | TCP + SSL/TLS | `127.0.0.1` | See [SSL and TLS](SSL-and-TLS) |
| `UdpServer` | UDP | `0.0.0.0` (all interfaces) | Each datagram is one request |
| `UnixSocketServer` | Unix domain socket | a new socket file in the temp directory | Like `TcpServer`, without TLS; see [Unix domain sockets](#unix-domain-sockets) |

You can also write your own; see [Custom Listeners](Custom-Listeners).

## TCP
```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("hello").Receive("world");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
Assert.Equal("world", await client.SendAndReceiveAsync("hello"));
```

Constructors:
```csharp
new TcpServer(port)                       // 127.0.0.1
new TcpServer("0.0.0.0", port)            // all interfaces
new TcpServer(IPAddress.IPv6Loopback, port)
new TcpServer(IPAddress.IPv6Any, port) { DualMode = true }   // IPv4 and IPv6, see below
```
`port` defaults to 3000 when you leave it out; use `0` for a free port.

TCP servers have two options, `KeepAlive` and `Framing`. They're described in [Connections and Framing](Connections-and-Framing).

## UDP
```csharp
using var server = new MockServer(new UdpServer("127.0.0.1", 0));
server.Mock.Send("hello").Receive("world");
server.Start();

Assert.Equal("world", await UdpTestClient.SendAndReceiveAsync(server.Port, "hello"));
```

Constructors:
```csharp
new UdpServer(port)                                        // 0.0.0.0
new UdpServer("127.0.0.1", port)
new UdpServer(new IPEndPoint(IPAddress.Loopback, port))
new UdpServer(new IPEndPoint(IPAddress.IPv6Any, port), dualMode: true)   // IPv4 and IPv6, see below
```

UDP differences:
- The socket is bound when the `UdpServer` is **created**, so a port that is already in use fails right away in the constructor.
  The server still only answers after `Start()`.
- Every datagram is one request, and the response goes back to the address and port the datagram came from.
- With no matching response, the server sends an **empty datagram**. With `NoReply()` or `Disconnect()` it sends nothing.

## IPv6 and dual-stack
IPv6 works like IPv4: pass an IPv6 address (or `"::1"`) to `TcpServer`, `TcpServerSsl` or `UdpServer`.

```csharp
using var server = new MockServer(new TcpServer(IPAddress.IPv6Loopback, 0));
server.Mock.Send("hello").Receive("world");
server.Start();

using var client = await TcpTestClient.ConnectAsync(IPAddress.IPv6Loopback, server.Port);
Assert.Equal("world", await client.SendAndReceiveAsync("hello"));
server.Should().HaveReceived("hello", Times.Once());
```

To serve IPv4 and IPv6 clients on one port, listen on `IPAddress.IPv6Any` in **dual mode**. For TCP set `DualMode = true`
before `Start()`:

```csharp
using var server = new MockServer(new TcpServer(IPAddress.IPv6Any, 0) { DualMode = true });
server.Mock.Send("hello").Receive("world");
server.Start();

using var viaIPv4 = await TcpTestClient.ConnectAsync(IPAddress.Loopback, server.Port);
using var viaIPv6 = await TcpTestClient.ConnectAsync(IPAddress.IPv6Loopback, server.Port);
Assert.Equal("world", await viaIPv4.SendAndReceiveAsync("hello"));
Assert.Equal("world", await viaIPv6.SendAndReceiveAsync("hello"));
server.Should().HaveReceived("hello", Times.Exactly(2));
```

`UdpServer` binds its socket in the constructor, so it takes the flag there:

```csharp
using var server = new MockServer(new UdpServer(new IPEndPoint(IPAddress.IPv6Any, 0), dualMode: true));
server.Mock.Send("hello").Receive("world");
server.Start();

Assert.Equal("world", await UdpTestClient.SendAndReceiveAsync(IPAddress.Loopback, server.Port, "hello"));
Assert.Equal("world", await UdpTestClient.SendAndReceiveAsync(IPAddress.IPv6Loopback, server.Port, "hello"));
server.Should().HaveReceived("hello", Times.Exactly(2));
```

Notes:
- IPv4 clients of a dual-mode server appear with **IPv4-mapped IPv6 addresses**, for example `::ffff:127.0.0.1`
  (`connection.RemoteEndPoint`, `ReceivedRequests[...].RemoteEndPoint`).
- Dual mode needs an IPv6 address: `TcpServer.Start()` throws `InvalidOperationException` and the `UdpServer` constructor
  throws `ArgumentException` when the address is IPv4. A restarted server keeps its dual mode.
- Tests that need IPv6 should check `Socket.OSSupportsIPv6` first.

## Unix domain sockets
`UnixSocketServer` listens on a socket file instead of a port. It has the features of `TcpServer`: persistent
connections, `KeepAlive`, `Framing`, connections, greetings, push, broadcasts and all failure simulation. It has no TLS.

```csharp
var listener = new UnixSocketServer();   // a new socket file in the temp directory; read it from listener.Path
using var server = new MockServer(listener);
server.Mock.Send("hello").Receive("world");
server.Start();

using var client = await TcpTestClient.ConnectUnixAsync(listener.Path);
Assert.Equal("world", await client.SendAndReceiveAsync("hello"));
server.Should().HaveReceived("hello", Times.Once());
Assert.True(File.Exists(listener.Path));
```

A client connects with `new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)` and
`Connect(new UnixDomainSocketEndPoint(listener.Path))`.

```csharp
new UnixSocketServer()                  // a unique file: <temp>/rony-xxxxxxxx.sock (the equivalent of port 0)
new UnixSocketServer("/tmp/my.sock")    // your own path
```

- **File lifecycle:** `Start()` creates the file, `Stop()` and `Dispose()` delete it, and a restart binds the same path
  again. A file which already exists at the path is **never deleted**: `Start()` fails with the platform's "address in
  use" `SocketException`. `RefuseConnections()` makes new clients fail to connect; `AcceptConnections()` brings the same
  path back.
- **Paths are short:** Unix socket paths are limited to about 104 bytes. A longer path fails at `Start()` with the
  platform's exception.
- **Differences from TCP:** `Address` is `IPAddress.None` and `Port` is `0` (meaningless here; `DualMode` has no effect).
  `ResetConnection()` / `connection.ResetAsync()` cannot send a TCP RST: the connection is just closed, so the client sees
  the end of the stream or a reset error, depending on the platform. A client's `RemoteEndPoint` has no useful address;
  logs print "unknown address" for it.
- **Platforms:** Unix domain sockets work on Linux and macOS, and on Windows 10 (version 1803) and later. Where they are not available
  `Start()` throws `PlatformNotSupportedException`; tests can check `Socket.OSSupportsUnixDomainSockets` first (.NET 8).

## Server properties
```csharp
using var server = new MockServer(new TcpServer("127.0.0.1", 0));
Assert.False(server.Active);

server.Start();

Assert.True(server.Active);
Assert.Equal("127.0.0.1", server.Address.ToString());
Assert.NotEqual(0, server.Port);
```

| Member | Description |
|---|---|
| `Start()` | Starts listening. Safe to call twice. |
| `Stop()` | Stops listening, closes open connections and cancels pending delayed responses. Safe to call twice. |
| `Dispose()` | Stops the server and releases the socket. Use `using var server = ...`. |
| `Active` | `true` while started. |
| `Address` | The address the server listens on. |
| `Port` | The port. With port `0`, read it after `Start()`. |
| `Mock` | Configures responses and records requests. |
| `ReceivedRequests` | Shortcut for `Mock.ReceivedRequests`. |

Runnable code: [`ServerSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ServerSamples.cs)
