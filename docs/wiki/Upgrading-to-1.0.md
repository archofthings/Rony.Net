# Upgrading to 1.0

Most 0.x code works unchanged. Check the items below, and see the
[changelog](https://github.com/archofthings/Rony.Net/blob/main/CHANGELOG.md) for the full list.

## TCP connections stay open
**Before:** the server closed the TCP connection after every response.
**Now:** the connection stays open, so a client can send more requests over it ([#1](https://github.com/archofthings/Rony.Net/issues/1)).

If your client reads until the server closes the connection, it will now wait. Restore the old behavior with:
```csharp
new TcpServer(port) { KeepAlive = false }
```
or close after specific responses:
```csharp
server.Mock.Send("GET").Receive("data").AndDisconnect();
```

Unmatched requests still close the connection, as before.

## `IListener.CloseAsync`
Custom listeners must implement the new method. If your transport has no connections, make it do nothing:
```csharp
public Task CloseAsync(object sender) => Task.CompletedTask;
```

## `Receive(...)` returns a builder
`Receive(...)` now returns a `ResponseBuilder` instead of `void`, to allow `.Then(...)`, `.After(...)` and the other chained calls.
Your source code doesn't change, but projects compiled against 0.x need to be rebuilt.

## `RequestHandler.Configs` (from 0.1.x)
`Configs` is a read-only dictionary keyed by request **bytes** (`IReadOnlyDictionary<byte[], Config>`), not a `Dictionary<string, Config>`.
Look entries up with `Configs["text".GetBytes()]`. Patterns and predicates aren't listed.

## Binary data (from 0.1.x)
Requests are matched on their exact bytes, and byte functions get the raw request. Binary payloads that were
corrupted in 0.1.x now work, so tests that depended on the old behavior may need updating.

## SSL certificates (from 0.1.x)
You can pass an `X509Certificate` directly, which is the most reliable option. The certificate name is also looked up in the `CurrentUser` store now.

## New in 1.0
Worth adopting while you upgrade:
- Port `0` for free ports: [Ports and Lifecycle](Ports-and-Lifecycle)
- `Verify` and `WaitForRequestAsync`: [Verifying Requests](Verifying-Requests), [Waiting for Requests](Waiting-for-Requests)
- Framing, patterns, sequences and failures: see the [Home](Home) page
