# Ports and Lifecycle

## Use port 0
A hard-coded port fails when another test or program is already using it, which happens often when tests run in parallel.
Pass `0` and the operating system picks a free port. Read it from `server.Port` after `Start()`:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Start();

Assert.InRange(server.Port, 1, 65535);
```

Then give `server.Port` to your client, for example through its configuration:
```csharp
var client = new QuoteClient(server.Port, TimeSpan.FromSeconds(1));
```

For `UdpServer` the port is known as soon as the server is created, because UDP binds right away.

## Many servers side by side
Each server gets its own free port, so tests that run in parallel never collide:

```csharp
var servers = Enumerable.Range(0, 5).Select(_ => new MockServer(new TcpServer(0))).ToList();
try
{
    foreach (var (server, index) in servers.Select((s, i) => (s, i)))
    {
        server.Mock.Send("who").Receive($"server {index}");
        server.Start();
    }

    for (var i = 0; i < servers.Count; i++)
    {
        using var client = await TcpTestClient.ConnectAsync(servers[i].Port);
        Assert.Equal($"server {i}", await client.SendAndReceiveAsync("who"));
    }
}
finally
{
    servers.ForEach(s => s.Dispose());
}
```

## Start, stop and restart
`Start()` and `Stop()` are safe to call more than once:

```csharp
using var server = new MockServer(new TcpServer(0));

server.Start();
server.Start();   // no-op
server.Stop();
server.Stop();    // no-op

Assert.False(server.Active);
```

A stopped server can be started again. A server created with port `0` keeps the port it was given,
so a client can reconnect to the same address. This is useful for testing reconnect logic:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("ping").Receive("pong");

server.Start();
var port = server.Port;
server.Stop();
server.Start();

Assert.Equal(port, server.Port);
```

What `Stop()` does:
- Stops accepting new connections and closes every open connection.
- Cancels responses that are still waiting on a delay ([`After`](Simulating-Failures#slow-responses)).
- Keeps the configured responses and recorded requests.

## Configure before or after Start
Responses can be added at any time, from any thread:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Start();

server.Mock.Send("late").Receive("still works");

using var client = await TcpTestClient.ConnectAsync(server.Port);
Assert.Equal("still works", await client.SendAndReceiveAsync("late"));
```

TCP options (`KeepAlive`, `Framing`) should be set before `Start()`. A connection uses the framing that was set when it opened.

## Dispose
Always dispose the server, most simply with `using var server = ...`. Disposing stops it and releases the socket,
so the port is free for the next test.

To share one server across the tests of a class, see [Recipes](Recipes#share-one-server-between-tests).

Runnable code: [`PortAndLifecycleSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/PortAndLifecycleSamples.cs)
