# Verifying Requests

The server records every request it receives, matched or not. Use this to check that your client sent the right thing.

## Verify
```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("LOGIN alice").Receive("OK");
server.Mock.Send("LIST").Receive("a,b,c");
server.Start();

using (var client = await TcpTestClient.ConnectAsync(server.Port))
{
    await client.SendAndReceiveAsync("LOGIN alice");
    await client.SendAndReceiveAsync("LIST");
    await client.SendAndReceiveAsync("LIST");
}

server.Mock.Verify("LOGIN alice");                       // at least once
server.Mock.Verify("LIST", Times.Exactly(2));
server.Mock.Verify("LOGOUT", Times.Never());
server.Mock.Verify(r => r.BodyString.StartsWith("LOGIN"), Times.Once());
```

`Verify` accepts:
- **Text:** `Verify("LIST")`, an exact match.
- **Bytes:** `Verify(new byte[] { 0x01, 0x02 })`, an exact match.
- **A predicate:** `Verify(r => ...)`, which gets each [`ReceivedRequest`](#inspecting-requests).

Without a `Times`, it checks that the request arrived at least once.

## Times
```csharp
server.Mock.Verify("x", Times.Exactly(2));
server.Mock.Verify("x", Times.AtLeast(1));
server.Mock.Verify("x", Times.AtMost(3));
server.Mock.Verify("x", Times.Between(1, 2));
server.Mock.Verify("x", Times.AtLeastOnce());
server.Mock.Verify("y", Times.Never());
```

| Times | Accepts |
|---|---|
| `Times.Never()` | 0 |
| `Times.Once()` | exactly 1 |
| `Times.AtLeastOnce()` | 1 or more |
| `Times.Exactly(n)` | exactly n |
| `Times.AtLeast(n)` | n or more |
| `Times.AtMost(n)` | 0 to n |
| `Times.Between(min, max)` | min to max, inclusive |

> If you also use Moq, which has its own `Times`, write `Rony.Net.Times` or add an alias: `using Times = Rony.Net.Times;`

## Failure messages
A failed check throws `MockVerificationException` with everything the server received, which usually shows the problem right away:

```csharp
server.Mock.Send("PING").Receive("PONG");
server.Mock.Match("PING");
server.Mock.Match("PNIG");

server.Mock.Verify("PING", Times.Exactly(2));
```
```
Rony.Net.MockVerificationException:
Expected request "PING" exactly 2 times, but it was received 1 time.
Received requests:
  1. "PING"
  2. "PNIG" (unmatched)
```

Binary requests are shown as hex, for example `0xFF 0x01`.

## Strict mode
`VerifyAllRequestsMatched()` fails if any request had no configured response. It catches requests your client sent
that you didn't expect:

```csharp
server.Mock.VerifyAllRequestsMatched();
```
```
Rony.Net.MockVerificationException:
1 request had no configured response:
  "DELETE everything"
```

A good habit is to call it at the end of a test, or in your test class's `Dispose`. The
[test framework packages](Test-Framework-Integration) do that for you with `VerifyAllRequestsMatchedAfterTest = true`.
A `Send("")` default response matches everything, so with one configured, strict mode always passes.
Requests answered by [`OnUnmatched()`](Request-Matching#unmatched-requests) still count as unmatched.

## Fail fast on unexpected requests
Strict mode reports unexpected requests at the end of the test. With `FailOnUnmatched`, the test fails as soon as
the server receives one: every following `Verify...` call throws, and so does every `WaitFor...` call, including one
that is already waiting. A typo then fails the test right away instead of after a long wait:

```csharp
server.Mock.FailOnUnmatched = true;
server.Mock.Send("HEARTBEAT").NoReply();
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("HEARTBAET");   // the bug under test

// Fails as soon as the typo arrives, instead of after 30 seconds.
var error = await Assert.ThrowsAsync<MockVerificationException>(
    () => server.Mock.WaitForRequestAsync("HEARTBEAT", TimeSpan.FromSeconds(30)));
```

## Order
`VerifyInOrder` checks that requests arrived in a given order. Other requests may come before, after or in between:

```csharp
// The client sent LOGIN bob, NOOP, LIST, QUIT
server.Mock.VerifyInOrder("LOGIN bob", "LIST", "QUIT");
server.Mock.VerifyInOrder(r => r.BodyString.StartsWith("LOGIN"), r => r.BodyString == "QUIT");
```

It takes text, bytes or predicates. When the order is wrong, the message says which request is missing:

```
Rony.Net.MockVerificationException:
Expected requests in order: "LOGIN bob", "LIST", but "LIST" was not received after "LOGIN bob".
Received requests:
  1. "LIST" (unmatched)
  2. "LOGIN bob" (unmatched)
```

To check the order on one connection only, look at that connection's
[`ReceivedRequests`](Connections-and-Push#inspecting-connections).

## Fluent assertions
`server.Should()` offers the same checks in a chain. Each one checks right away and throws `MockVerificationException`:

```csharp
server.Should().HaveReceived("LOGIN bob", Times.Once())
    .And.HaveReceived("LIST")
    .And.NotHaveReceived("DELETE")
    .And.HaveReceivedInOrder("LOGIN bob", "LIST")
    .And.HaveNoUnmatchedRequests()
    .And.HaveAcceptedConnections(Times.Once());
```

| Assertion | Same as |
|---|---|
| `HaveReceived(request or predicate[, times])` | `Verify(...)`; at least once without `times` |
| `NotHaveReceived(request or predicate)` | `Verify(..., Times.Never())` |
| `HaveReceivedInOrder(...)` | `VerifyInOrder(...)` |
| `HaveNoUnmatchedRequests()` | `VerifyAllRequestsMatched()` |
| `HaveAcceptedConnections(times)` | `server.VerifyConnections(times)` ([TCP](Connections-and-Push)) |
| `BeInState(state)` | `Assert.Equal(state, server.Mock.State)` ([scenarios](Stateful-Scenarios)) |

`Should()` is a method of `MockServer`, so it works next to FluentAssertions or Shouldly without conflicts.

## Inspecting requests
`server.ReceivedRequests`, or `server.Mock.ReceivedRequests`, lists every request, oldest first:

```csharp
IReadOnlyList<ReceivedRequest> requests = server.ReceivedRequests;
Assert.Equal(new[] { "first", "second" }, requests.Select(r => r.BodyString));
Assert.All(requests, r => Assert.True(r.Matched));
Assert.All(requests, r => Assert.NotNull(r.RemoteEndPoint));
Assert.True(requests[0].Timestamp <= requests[1].Timestamp);
```

| `ReceivedRequest` | |
|---|---|
| `Body` | The request bytes, with framing removed |
| `BodyString` | The request as UTF-8 text |
| `RemoteEndPoint` | The client's address and port |
| `Timestamp` | When the server received it |
| `Matched` | Whether a configured response handled it |
| `ConnectionId` | The [connection](Connections-and-Push) it arrived on (TCP); `null` for UDP and `Match(...)` |

`server.Mock.UnmatchedRequests` lists only the requests that had no response.

## Starting over
`ClearReceivedRequests()` forgets what was received but keeps the configuration, which is useful between the steps of a longer test:

```csharp
server.Mock.ClearReceivedRequests();

server.Mock.Verify("ping", Times.Never());
```

`Reset()` clears both the recorded requests and the configuration.

## Timing
`Verify` checks what has been received **so far**. If your client sends in the background, the request might not have
arrived yet when you verify. In that case, [wait for it](Waiting-for-Requests) first.

Runnable code: [`VerificationSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/VerificationSamples.cs)
