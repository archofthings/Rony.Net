# Backlog

Proposed work for Rony.Net after 1.2.0. Nothing here is started or promised. The numbers are the ones used in the
1.1 planning, so they have gaps (1–5, 12 and 14 shipped in 1.1.0; 6, 7, 8 and 16 in 1.2.0; see `CHANGELOG.md`).

To start an item: `/new-feature <item>` gives the checklist. When an item ships, remove it here and describe it in
`CHANGELOG.md`.

## Suggested order

| Order | Item | Why this position |
|---|---|---|
| 1 | 9. Partial matching | Touches the matching order in `RequestHandler.cs`; design first |
| 2 | 10. TLS extras | Self-contained in `TcpServerSsl.cs` |
| 3 | 11. IPv6 and Unix domain sockets | Listener work; Unix sockets need a `#if` for .NET Standard 2.1 |
| 4 | 13. Record and replay, 15. Configuration files and CLI | Large; each needs its own design, probably its own package |

## Library features

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
