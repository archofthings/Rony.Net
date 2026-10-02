# Backlog

Proposed work for Rony.Net after 1.1.0. Nothing here is started or promised. The numbers are the ones used in the
1.1 planning, so they have gaps (1–5, 12 and 14 shipped in 1.1.0; see `CHANGELOG.md`).

To start an item: `/new-feature <item>` gives the checklist. When an item ships, remove it here and describe it in
`CHANGELOG.md`.

## Suggested order

| Order | Item | Why this position |
|---|---|---|
| 1 | Small follow-ups (below) | Cheap, and they close out 1.1 |
| 2 | 16. `IAsyncDisposable` and `StartAsync()` | Small API addition that removes a source of flaky tests |
| 3 | 7. More failure modes | Small, and the most asked-for kind of mock behaviour |
| 4 | 6. Chunked or slow responses | Builds on the response chain; pairs with 7 |
| 5 | 8. More framings | Self-contained in `MessageFraming.cs` |
| 6 | 9. Partial matching | Touches the matching order in `RequestHandler.cs`; design first |
| 7 | 10. TLS extras | Self-contained in `TcpServerSsl.cs` |
| 8 | 11. IPv6 and Unix domain sockets | Listener work; Unix sockets need a `#if` for .NET Standard 2.1 |
| 9 | 13. Record and replay, 15. Configuration files and CLI | Large; each needs its own design, probably its own package |

## Library features

### 6. Chunked or slow responses
Send a response in pieces with delays between them, or throttle the bandwidth, so that a client's partial reads
and buffering can be tested.

### 7. More failure modes
- Connection reset (RST, through `LingerState(true, 0)`) instead of a clean close.
- Corrupted or truncated responses.
- Refusing new connections while the server is running.
- A forced TLS handshake failure.

### 8. More framings
- Little-endian length prefix.
- A length prefix that includes its own size.
- Fixed-length messages.
- STX/ETX.

### 9. Partial matching
- JSON field matching: `SendJson(j => ...)`.
- Regex capture groups passed to the response: `Receive(m => $"HELLO {m.Groups[1]}")`.

### 10. TLS extras
- Mutual TLS: require and validate a client certificate.
- Assert the negotiated protocol and the SNI host name.
- A built-in `TestCertificate.CreateSelfSigned()`. Today a helper exists only in the samples and tests
  (`samples/Rony.Samples/TestCertificates.cs`, `tests/Rony.FunctionalTests/TestCertificate.cs`).

### 11. More endpoints
- IPv6 and any-address binding.
- Unix domain sockets.

### 13. Record and replay
A proxy mode that records the traffic between a client and the real server to a file, and replays it as mock
configuration.

### 15. Configuration files and a standalone server
- Mock configuration from JSON or YAML.
- A standalone CLI and Docker image that run a mock server from such a file.

### 16. Async lifecycle
`IAsyncDisposable`, and a `StartAsync()` that completes when the socket is listening.

## Smaller follow-ups from 1.1
- [ ] `Rony.Net.Xunit.v3` package: xUnit v3 moved `ITestOutputHelper` into the `Xunit` namespace, so the v2 package
      does not work with it.
- [ ] Per-connection fluent assertions: `connection.Should().HaveReceivedInOrder(...)`.
- [ ] Possibly `server.Should().HaveNoOpenConnections()`. It needs wait semantics, otherwise tests that use it
      become flaky.
- [ ] Confirm that `Rony.Net.Xunit`, `Rony.Net.NUnit` and `Rony.Net.MSTest` 1.1.0 finished nuget.org validation
      and are listed.

## Repository housekeeping
- [ ] Bump `actions/checkout` and `actions/setup-dotnet` to v5 in the three workflows (the release log shows a
      Node 20 deprecation warning).
- [ ] Add an `.editorconfig` and make the code pass `dotnet format --verify-no-changes` (it reports 6 issues
      today), then check it in CI.
- [ ] Move the package metadata that the four projects repeat (authors, license, repository, Source Link
      settings) into a `src/Directory.Build.props`.
