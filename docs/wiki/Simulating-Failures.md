# Simulating Failures

Real servers are slow, drop connections and stop answering. Rony.Net lets you test how your client handles each of these.

| Method | What the client sees |
|---|---|
| `.After(delay)` | The response arrives after `delay` |
| `NoReply()` / `.ThenNoReply()` | Nothing; the connection stays open (a timeout) |
| `Disconnect()` / `.ThenDisconnect()` | The connection closes without a response |
| `.AndDisconnect()` | The response, then the connection closes |

`Disconnect()` and `.AndDisconnect()` only apply to TCP; for UDP, `Disconnect()` behaves like `NoReply()`.

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
- **Server not running:** call `server.Stop()`. Connections are refused, and existing ones are closed.
- **Server comes back:** call `server.Start()` again. With port `0`, it returns on the same port.
- **Garbage or error responses:** configure them like any other response, for example `Receive(new byte[] { 0xFF, 0xFF })` or `Receive("ERR 500")`.

Runnable code: [`SequenceAndFailureSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/SequenceAndFailureSamples.cs)
