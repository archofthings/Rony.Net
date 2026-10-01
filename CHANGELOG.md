# Changelog

## 0.3.0

### Added
- **Persistent TCP connections.** A client can send many requests over one connection, for TCP and TCP + SSL/TLS
  ([#1](https://github.com/archofthings/Rony.Net/issues/1)). Every connection is handled independently, so a client
  that never sends anything no longer blocks other clients. Set `KeepAlive = false` to close after every response.
- **Message framing** for TCP: `MessageFraming.Delimiter("\n")` and `MessageFraming.LengthPrefix(2)` split the stream
  into messages and frame responses. You can also implement `IMessageFraming` yourself.
- **Automatic ports.** Pass port `0` and read `server.Port` after `Start()`. The port is kept across restarts.
- **Flexible matching:** `Send(Regex)`, `SendMatching(Func<string, bool>)` and `SendMatchingBytes(Func<byte[], bool>)`.
  Exact requests win over patterns and predicates, which win over the "any request" config.
- **Response sequences:** `Receive("busy").Then("busy").Then("ok")`. The last response repeats once the sequence ends.
- **Simulating failures:** `.After(delay)`, `Disconnect()`, `NoReply()`, `.AndDisconnect()`, `ThenDisconnect()`, `ThenNoReply()`.
- **Request recording and verification:** `ReceivedRequests`, `UnmatchedRequests`, `Verify(...)` with `Times`,
  `VerifyAllRequestsMatched()`, `WaitForRequestAsync(...)`, `WaitForRequestsAsync(count)`, `ClearReceivedRequests()` and `Reset()`.

### Changed (breaking)
- TCP connections are no longer closed after each response. Clients that read until the server closes the connection
  should set `KeepAlive = false` on the server, or use `.AndDisconnect()` on the response.
- `IListener` has a new `CloseAsync(object sender)` method. Custom listeners need to implement it.
- `Receive(...)` returns a `ResponseBuilder` instead of `void`. Existing code still compiles, but needs a rebuild.
- `TcpServer` and `TcpServerSsl` now derive from `TcpServerBase`.
- A TCP client that connects and disconnects without sending anything no longer produces an empty request.

## 0.2.0

### Changed
- Targets `netstandard2.1` and `net8.0` (was `netcoreapp3.1`).
- Requests are matched on raw bytes, so binary payloads that aren't valid UTF-8 work correctly.
- The server keeps running when a client misbehaves, and `Start()`/`Stop()` can be called repeatedly.
- `TcpServerSsl` accepts an `X509Certificate` directly.
- Mock configuration is thread-safe.
- `RequestHandler.Configs` is an `IReadOnlyDictionary<byte[], Config>` (breaking).
