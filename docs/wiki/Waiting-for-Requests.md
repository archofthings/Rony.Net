# Waiting for Requests

When your code sends in the background (a heartbeat, a log shipper, a fire-and-forget message), a test needs to wait
for the request to arrive. Don't use `Thread.Sleep` or `Task.Delay`: they make tests slow, and still flaky.
Wait for the request itself instead:

```csharp
using var server = new MockServer(new UdpServer("127.0.0.1", 0));
server.Mock.Send("").NoReply();
server.Start();

StartHeartbeat(server.Port);   // your code, sending in the background

var request = await server.Mock.WaitForRequestAsync("HEARTBEAT", TimeSpan.FromSeconds(5));

Assert.Equal("HEARTBEAT", request.BodyString);
```

## The methods
```csharp
await server.Mock.WaitForRequestAsync();                               // any request
await server.Mock.WaitForRequestAsync("HEARTBEAT");                    // exact text
await server.Mock.WaitForRequestAsync(new byte[] { 0x01 });            // exact bytes
await server.Mock.WaitForRequestAsync(r => r.Body.Length > 100);       // a predicate
var requests = await server.Mock.WaitForRequestsAsync(count: 3);       // at least 3 requests in total
```

- They return the matching [`ReceivedRequest`](Verifying-Requests#inspecting-requests), or the list of requests for `WaitForRequestsAsync`.
- They include requests that arrived **before** you started waiting, so there is no race between sending and waiting.
- The timeout defaults to **5 seconds**. Pass your own as the second argument.
- All of them accept a `CancellationToken` as the last argument.

To wait for connections instead, see [Connections and Push](Connections-and-Push#checking-how-your-client-uses-connections):
`server.WaitForConnectionAsync()`, `server.WaitForConnectionsAsync(count)` and `connection.WaitForCloseAsync()`.

With [`FailOnUnmatched`](Verifying-Requests#fail-fast-on-unexpected-requests), a wait stops with a
`MockVerificationException` as soon as an unexpected request arrives.

## Timeouts
When the timeout expires, a `TimeoutException` explains what was expected and lists what did arrive:

```csharp
var error = await Assert.ThrowsAsync<TimeoutException>(
    () => server.Mock.WaitForRequestAsync("expected", TimeSpan.FromMilliseconds(200)));
```
```
Expected request "expected" within 00:00:00.2000000, but it was not received.
Received requests:
  1. "something else" (unmatched)
```

## Waiting, then verifying
Wait for the last request you expect, then verify the rest:

```csharp
await server.Mock.WaitForRequestAsync("COMMIT");
server.Should().HaveReceived("BEGIN", Times.Once());
server.Should().HaveReceived(r => r.BodyString.StartsWith("INSERT"), Times.Exactly(3));
```

Runnable code: [`VerificationSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/VerificationSamples.cs)
