# Connections and Push

Many protocols aren't strictly "client asks, server answers". SMTP, FTP and POP3 servers greet the client first,
chat and trading servers push messages the client never asked for, and good clients reuse their connections and
close them when they are done. TCP servers (`TcpServer` and `TcpServerSsl`) let you mock and check all of that.

UDP has no connections, so the features on this page are TCP only. On a `UdpServer`, `Connections` is always
empty and the other members throw `NotSupportedException`.

## Greetings: talk first
`OnConnect()` configures what the server sends as soon as a client connects, before the client sends anything:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.OnConnect().Receive("220 mail.test ESMTP ready\r\n");
server.Mock.Send("QUIT\r\n").Receive("221 bye\r\n").AndDisconnect();
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
Assert.Equal("220 mail.test ESMTP ready\r\n", await client.ReceiveAsync());   // before sending anything
Assert.Equal("221 bye\r\n", await client.SendAndReceiveAsync("QUIT\r\n"));
```

A greeting takes everything a response takes: `Then(...)` for a different greeting per connection, `.After(delay)`
for a slow server, `.AndDisconnect()`, and [`GoTo(...)`](Stateful-Scenarios) to start a session. The greeting is
always sent before the response to the client's first request. With [framing](Connections-and-Framing), it is framed
like a response.

```csharp
server.Mock.OnConnect()
    .Receive("200 welcome")
    .Then("421 too many connections").AndDisconnect();   // the second and later clients are turned away
```

To refuse every connection, disconnect right away:

```csharp
server.Mock.OnConnect().Disconnect();
```

## Inspecting connections
`server.Connections` lists every connection the server accepted, open or closed, oldest first.
`server.OpenConnections` lists the ones still open.

```csharp
ClientConnection connection = server.Connections.Single();
Assert.Equal(1, connection.Id);
Assert.NotNull(connection.RemoteEndPoint);
Assert.Equal(new[] { "first", "second" }, connection.ReceivedRequests.Select(r => r.BodyString));
Assert.Equal(connection.Id, server.ReceivedRequests[0].ConnectionId);
```

| `ClientConnection` | |
|---|---|
| `Id` | 1 for the first connection, 2 for the next, ... Shown as `#1` in the [log](Logging-and-Diagnostics). |
| `RemoteEndPoint` | The client's address and port |
| `ConnectedAt`, `ClosedAt` | When it was accepted and closed (`null` while open) |
| `IsOpen` | Whether it is still open |
| `ReceivedRequests` | The requests received on this connection |
| `State` | Its [scenario state](Stateful-Scenarios) |
| `Tls` | The protocol, server name and client certificate of a TLS connection, `null` otherwise: [SSL and TLS](SSL-and-TLS#checking-protocol-server-name-and-client-certificate) |
| `SendAsync(string or byte[])` | [Pushes a message](#pushing-messages) to the client |
| `CloseAsync()` | Closes the connection from the server side |
| `WaitForCloseAsync(timeout)` | Waits until the connection is closed |

Every [`ReceivedRequest`](Verifying-Requests#inspecting-requests) has a `ConnectionId`, too.

## Checking how your client uses connections
**Does it reuse its connection?**
```csharp
await client.SendAndReceiveAsync("one");
await client.SendAndReceiveAsync("two");

server.Should().HaveAcceptedConnections(Times.Once());
```

**Does it close its connection?** The server notices a closed connection a moment after the client closes it,
so wait for it instead of checking right away:
```csharp
var connection = await server.WaitForConnectionAsync();
await connection.WaitForCloseAsync();   // throws TimeoutException after 5 seconds if it stays open
Assert.Empty(server.OpenConnections);
```

## Waiting for all connections to close
When your test starts several clients, wait until none is open instead of waiting for each connection:
```csharp
await server.WaitForAllConnectionsClosedAsync();   // returns at once if none is open
server.Should().HaveNoOpenConnections();
```
`HaveNoOpenConnections()` checks immediately; its failure message lists the open connections and reminds you to
await `WaitForAllConnectionsClosedAsync()` first. The wait throws `TimeoutException` (default 5 seconds) that lists
the connections still open.

To assert on what one connection received, use [`connection.Should()`](Verifying-Requests#assertions-on-one-connection).

| Member | |
|---|---|
| `server.Should().HaveAcceptedConnections(Times times)` | How many connections were accepted. Throws `MockVerificationException`. (Classic: `server.VerifyConnections(times)`.) |
| `server.WaitForConnectionAsync(timeout)` | Waits for the first connection |
| `server.WaitForConnectionsAsync(count, timeout)` | Waits until `count` connections were accepted |
| `server.WaitForAllConnectionsClosedAsync(timeout)` | Waits until no connection is open |
| `server.Should().HaveNoOpenConnections()` | No connection is open right now |
| `connection.WaitForCloseAsync(timeout)` | Waits until the connection is closed |
| `connection.Should()` | [Assertions on one connection](Verifying-Requests#assertions-on-one-connection) |

The timeouts default to 5 seconds, and every wait also takes a `CancellationToken`.

## Connection events
```csharp
server.ConnectionOpened += (sender, connection) => { /* before its first request is handled */ };
server.ConnectionClosed += (sender, connection) => { /* closed by either side; ClosedAt is set */ };
```
The events are raised on the server's background threads. An exception thrown by a handler is
[logged](Logging-and-Diagnostics) and otherwise ignored. `WaitForConnectionAsync()` and `WaitForConnectionsAsync()` complete
after the `ConnectionOpened` handlers have returned, so a handler must not wait for something the test does after the wait.

## Pushing messages
Send a message the client didn't ask for, such as a notification, to one connection:

```csharp
using var client = await TcpTestClient.ConnectAsync(server.Port);
var connection = await server.WaitForConnectionAsync();

await connection.SendAsync("NOTIFY price-changed");

Assert.Equal("NOTIFY price-changed", await client.ReceiveAsync());
```

Or to every open connection:

```csharp
var sent = await server.BroadcastAsync("SHUTDOWN in 5 minutes");   // returns how many connections it reached
```

Pushed messages are [framed](Connections-and-Framing) like responses, so with `MessageFraming.Delimiter("\n")` the
client receives `"SHUTDOWN in 5 minutes\n"`. A push doesn't wait for pending (for example delayed) responses on
that connection, just like a real server. Pushing to a closed connection throws `InvalidOperationException`.

## Closing a connection from the server
```csharp
await connection.CloseAsync();
```
To close a connection in response to a request, use [`Disconnect()` or `.AndDisconnect()`](Simulating-Failures) instead.

Runnable code: [`ConnectionAndPushSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ConnectionAndPushSamples.cs)
