# Backlog

Proposed work for Rony.Net after 1.1.0. Nothing here is started or promised. The numbers are the ones used in the
1.1 planning, so they have gaps (1–5, 12 and 14 shipped in 1.1.0; see `CHANGELOG.md`).

To start an item: `/new-feature <item>` gives the checklist. When an item ships, remove it here and describe it in
`CHANGELOG.md`.

## Suggested order

| Order | Item | Why this position |
|---|---|---|
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

## Repository housekeeping
- [ ] Make the old tests follow the test rules. `MockTcpServerTests`, `MockTcpServerSslTests` and
      `MockUdpServerTests` use hard-coded ports, and two burst tests open about 15,000 connections, so two full
      runs within 30 seconds exhaust the ephemeral ports on macOS ("Can't assign requested address").
      `ConnectionAndFramingSamples.cs` and `VerificationSamples.cs` wait with `Task.Delay`.
- [ ] Bump `actions/checkout` and `actions/setup-dotnet` to v5 in the three workflows (the release log shows a
      Node 20 deprecation warning).
- [ ] Add an `.editorconfig` and make the code pass `dotnet format --verify-no-changes` (it reports 6 issues
      today), then check it in CI.
- [ ] Move the package metadata that the four projects repeat (authors, license, repository, Source Link
      settings) into a `src/Directory.Build.props`.
