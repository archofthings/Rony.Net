# Servers

A `MockServer` runs on a *listener*, which is the transport. Rony.Net includes three:

| Listener | Protocol | Default address | Notes |
|---|---|---|---|
| `TcpServer` | TCP | `127.0.0.1` | Connections stay open across requests |
| `TcpServerSsl` | TCP + SSL/TLS | `127.0.0.1` | See [SSL and TLS](SSL-and-TLS) |
| `UdpServer` | UDP | `0.0.0.0` (all interfaces) | Each datagram is one request |

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
```

UDP differences:
- The socket is bound when the `UdpServer` is **created**, so a port that is already in use fails right away in the constructor.
  The server still only answers after `Start()`.
- Every datagram is one request, and the response goes back to the address and port the datagram came from.
- With no matching response, the server sends an **empty datagram**. With `NoReply()` or `Disconnect()` it sends nothing.

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
