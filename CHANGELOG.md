# Changelog

Full documentation: [Rony.Net wiki](https://github.com/archofthings/Rony.Net/wiki).

## 1.4.0

No breaking changes.

### Added
- **TLS extras:** `TestCertificate.CreateSelfSigned(subjectName)` creates an in-memory self-signed certificate for servers
  and clients; `TcpServerSsl.RequireClientCertificate` and `ClientCertificateValidator` add mutual TLS (a rejected client
  fails the handshake and is logged); `connection.Tls` (`TlsConnectionInfo`: protocol, SNI server name, client
  certificate) and the assertions `HaveUsedTls`, `HaveServerName` and `HavePresentedClientCertificate` check them. Custom
  listeners opt in with the new `ITlsListener`. The connect log line of a TLS connection now ends with the protocol,
  server name and client certificate.
- **More endpoints:** IPv6 is official (`new TcpServer(IPAddress.IPv6Loopback, 0)`, also for TLS and UDP); `TcpServerBase.DualMode`
  and `new UdpServer(endPoint, dualMode: true)` serve IPv4 and IPv6 clients on one port (IPv4 clients appear as IPv4-mapped
  addresses); the new `UnixSocketServer` listens on a Unix domain socket file with the features of `TcpServer`
  (no TLS; a reset just closes the connection).
- **Configuration files:** `MockServer.FromFile(path)` and `MockServer.FromJson(json)` create a server (TCP, TLS, UDP or Unix
  socket, with framing) and its rules from a JSON file: exact, regular expression and JSON matching, greetings, sequences,
  delays, disconnects, resets, silence and scenario states. Mistakes are reported as a `FormatException` naming the
  place, and unknown properties are rejected so typos are found. The returned server can be extended in code.
- **Standalone server:** the new `Rony.Net.Cli` package is the `rony` .NET tool (`dotnet tool install --global Rony.Net.Cli`)
  and a Dockerfile: `rony run <config.json>` serves a configuration file, `rony validate` checks it, `rony record` records a
  real server through a proxy (TLS to and from the proxy, framing options) and `rony replay` serves the recording.
- **Buffer limit:** `TcpServerBase.MaxBufferedBytes` limits the bytes a connection buffers while waiting for a complete message
  (`0`, the default, is unlimited); configuration files have `server.maxBufferedBytes`, default 16 MiB, and `rony replay` uses 16 MiB.

### Changed
- A length-prefixed message with an invalid (negative) 4-byte length now closes that connection and reports it instead of
  buffering without end. Connection details in log lines (server name, certificate subject, remote endpoint, exception
  messages) are escaped and cut at 256 characters, so a client cannot inject lines or terminal sequences into the log.

### Documentation
- New wiki page [Known Issues and Limitations](https://github.com/archofthings/Rony.Net/wiki/Known-Issues), summarised in the README.
- The wiki page [Standalone Server](https://github.com/archofthings/Rony.Net/wiki/Standalone-Server) has walk-throughs for the `rony` tool: development stand-in, teams without .NET, record and replay (also TLS), CI, containers and Docker Compose, stateful and failing servers, Unix socket and UDP, one file for the tool and tests.

## 1.3.0

No breaking changes.

### Added
- **Partial matching:** `SendJson(j => j["type"].AsString() == "login")` matches requests by JSON content (the new
  dependency-free `JsonData`, also usable with `JsonData.Parse`), and `ReceiveMatch(m => ...)` / `ThenMatch(m => ...)`
  build a response from the capture groups of a `Send(Regex)` rule.
- **Record and replay:** `RecordingProxy` relays TCP or TLS traffic to a real server and records it into a `Recording`
  (`Save` / `Load` as JSON you can edit by hand); `server.Replay(recording)` turns it into rules: greeting, replies, sequences for
  repeated requests, silence and disconnects.

## 1.2.0

No breaking changes.

### Added
- **Async lifecycle:** `server.StartAsync()`, `server.StopAsync()` and `await using` (`DisposeAsync()`). `StopAsync()` waits
  for requests, delayed responses and connections in flight, so no `Log` line or callback runs after it completed.
- **Per-connection assertions:** `connection.Should()` with `HaveReceived`, `NotHaveReceived`, `HaveReceivedInOrder`,
  `BeInState`, `BeOpen` and `BeClosed`; only requests received on that connection count, and failure messages name it.
- **No open connections:** `server.WaitForAllConnectionsClosedAsync()` and `server.Should().HaveNoOpenConnections()`.
- **More failure modes:** `ResetConnection()`, `ThenResetConnection()`, `AndResetConnection()` and `connection.ResetAsync()`
  abort a TCP connection with a reset (RST); `Truncated(byteCount)` and `Corrupted(func)` change a response as it goes on
  the wire; `server.RefuseConnections()` / `AcceptConnections()` make new clients get "connection refused" while open
  connections keep working; `TcpServerSsl.FailHandshake` makes every TLS handshake fail. Custom listeners opt in with
  the new `IFaultInjectionListener`.
- **Chunked and slow responses:** `InChunks(chunkSize, delay)` sends a response in pieces with a wait between them and
  `Throttled(bytesPerSecond)` sends it at about that rate. A message pushed meanwhile waits for the last piece, and
  `StopAsync()` ends a slow response at once. `IFaultInjectionListener` gains a chunked `SendRawAsync` overload (the
  interface is new in 1.2).
- **More framings:** `MessageFraming.LengthPrefix(prefixLength, bigEndian, includesPrefix)` for lengths that count the
  prefix, `FixedLength(length, padding)`, `StartEnd(start, end)` and `StxEtx`. The little-endian length prefix
  (`bigEndian: false`) existed before and is now documented.
- **xUnit v3:** the `Rony.Net.Xunit.v3` package, the same `MockServerTest` and `LogTo(ITestOutputHelper)` as
  `Rony.Net.Xunit` (which stays for xUnit v2) in the same `Rony.Net.Xunit` namespace.

## 1.1.0

No breaking changes: code written for 1.0 compiles and behaves the same.

### Added
- **Greetings:** `Mock.OnConnect().Receive(...)` sends a message as soon as a client connects, before its first
  request. Use `Then(...)` for a different greeting per connection, or `OnConnect().Disconnect()` to refuse connections.
- **Connections:** `server.Connections` and `OpenConnections` (`ClientConnection` with `Id`, `RemoteEndPoint`,
  `ConnectedAt`, `ClosedAt`, `IsOpen`, `ReceivedRequests`), `ConnectionOpened`/`ConnectionClosed` events,
  `WaitForConnectionAsync()`, `WaitForConnectionsAsync(count)`, `connection.WaitForCloseAsync()` and
  `VerifyConnections(Times)`. `ReceivedRequest.ConnectionId` tells which connection a request arrived on.
- **Pushed messages:** `connection.SendAsync(...)`, `server.BroadcastAsync(...)` and `connection.CloseAsync()`.
- **Stateful scenarios:** `Mock.InState("state").Send(...)` rules and `.GoTo("state")` transitions, a settable
  `Mock.State`, and `Mock.StateScope = StateScope.Connection` for a state per connection.
- **Unmatched requests:** `Mock.OnUnmatched()` chooses the reaction (reply, stay silent, disconnect) while the requests
  still count as unmatched; `Mock.FailOnUnmatched = true` makes every `Verify...` and `WaitFor...` call throw as soon
  as an unmatched request arrives.
- **Logging:** `server.Log` receives a line for every connection, request (with the rule that matched it), response,
  state change and error, including exceptions thrown by `Receive(...)` functions and predicates, and failed TLS handshakes.
- **Ordered verification:** `Mock.VerifyInOrder(...)` with text, bytes or predicates.
- **Fluent assertions:** `server.Should().HaveReceived(...).And.HaveReceivedInOrder(...)` and more.
- **Test framework packages:** `Rony.Net.Xunit`, `Rony.Net.NUnit` and `Rony.Net.MSTest` (MSTest 4), each with a
  `MockServerTest` base class (a started server per test that logs to the test output) and a log extension method.
- `IConnectionListener`, implemented by `TcpServer` and `TcpServerSsl`, for custom listeners that want the connection
  features. Listeners that only implement `IListener` keep working.

### Fixed
- A TCP connection that got a request without a reply (`NoReply()`, `Disconnect()`) and was then closed by the client
  was not released until the server stopped.

## 1.0.0

First stable release. The public API follows [semantic versioning](https://semver.org) from here on.

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
- **IntelliSense documentation** ships with the package, and Source Link plus a symbols package (`.snupkg`)
  let you step into the library while debugging.
- A runnable [samples project](samples/Rony.Samples) with every example from the wiki.

### Changed (breaking, compared to 0.2.0)
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
