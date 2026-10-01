# Verifying Requests

The server records every request it receives, matched or not. Use this to check that your client sent the right thing.
`server.Should()` starts a chain of assertions; each one checks right away and throws `MockVerificationException`
when it fails.

## Checking requests
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

server.Should().HaveReceived("LOGIN alice");                       // at least once
server.Should().HaveReceived("LIST", Times.Exactly(2));
server.Should().NotHaveReceived("LOGOUT");
server.Should().HaveReceived(r => r.BodyString.StartsWith("LOGIN"), Times.Once());
```

`HaveReceived` and `NotHaveReceived` accept:
- **Text:** `HaveReceived("LIST")`, an exact match.
- **Bytes:** `HaveReceived(new byte[] { 0x01, 0x02 })`, an exact match.
- **A predicate:** `HaveReceived(r => ...)`, which gets each [`ReceivedRequest`](#inspecting-requests).

Without a `Times`, `HaveReceived` checks that the request arrived at least once.

Chain several checks with `And`:
```csharp
server.Should().HaveReceived("LOGIN bob", Times.Once())
    .And.HaveReceived("LIST")
    .And.NotHaveReceived("DELETE")
    .And.HaveReceivedInOrder("LOGIN bob", "LIST")
    .And.HaveNoUnmatchedRequests()
    .And.HaveAcceptedConnections(Times.Once());
```

## Times
```csharp
server.Should().HaveReceived("x", Times.Exactly(2));
server.Should().HaveReceived("x", Times.AtLeast(1));
server.Should().HaveReceived("x", Times.AtMost(3));
server.Should().HaveReceived("x", Times.Between(1, 2));
server.Should().HaveReceived("x", Times.AtLeastOnce());
server.Should().NotHaveReceived("y");
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

server.Should().HaveReceived("PING", Times.Exactly(2));
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
`HaveNoUnmatchedRequests()` fails if any request had no configured response. It catches requests your client sent
that you didn't expect:

```csharp
server.Should().HaveNoUnmatchedRequests();
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
the server receives one: every following check (`Should()...` or `Verify...`) throws, and so does every `WaitFor...` call, including one
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
`HaveReceivedInOrder` checks that requests arrived in a given order. Other requests may come before, after or in between:

```csharp
// The client sent LOGIN bob, NOOP, LIST, QUIT
server.Should().HaveReceivedInOrder("LOGIN bob", "LIST", "QUIT");
server.Should().HaveReceivedInOrder(r => r.BodyString.StartsWith("LOGIN"), r => r.BodyString == "QUIT");
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

## All assertions
| Assertion | Classic method |
|---|---|
| `HaveReceived(request or predicate[, times])` | `server.Mock.Verify(...)`; at least once without `times` |
| `NotHaveReceived(request or predicate)` | `server.Mock.Verify(..., Times.Never())` |
| `HaveReceivedInOrder(...)` | `server.Mock.VerifyInOrder(...)` |
| `HaveNoUnmatchedRequests()` | `server.Mock.VerifyAllRequestsMatched()` |
| `HaveAcceptedConnections(times)` | `server.VerifyConnections(times)` ([TCP](Connections-and-Push)) |
| `BeInState(state)` | `Assert.Equal(state, server.Mock.State)` ([scenarios](Stateful-Scenarios)) |

The classic methods do the same checks with the same messages, if you prefer that style or are upgrading from 1.0.
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

server.Should().NotHaveReceived("ping");
```

`Reset()` clears both the recorded requests and the configuration.

## Timing
An assertion checks what has been received **so far**. If your client sends in the background, the request might not have
arrived yet when you verify. In that case, [wait for it](Waiting-for-Requests) first.

Runnable code: [`VerificationSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/VerificationSamples.cs)
