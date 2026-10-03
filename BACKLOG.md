# Backlog

Proposed work for Rony.Net after 1.3.0. Nothing here is started or promised. The numbers are the ones used in the
1.1 planning, so they have gaps (1–5, 12 and 14 shipped in 1.1.0; 6, 7, 8 and 16 in 1.2.0; 9 and 13 in 1.3.0; 10 is done for 1.4.0; see `CHANGELOG.md`).

To start an item: `/new-feature <item>` gives the checklist. When an item ships, remove it here and describe it in
`CHANGELOG.md`.

## Suggested order

| Order | Item | Why this position |
|---|---|---|
| 1 | 11. IPv6 and Unix domain sockets | Listener work; Unix sockets need a `#if` for .NET Standard 2.1 |
| 2 | 15. Configuration files and CLI | Large; needs its own design, probably its own package |

## Library features

### 11. More endpoints
- IPv6 and any-address binding.
- Unix domain sockets.

### 15. Configuration files and a standalone server
- Mock configuration from JSON or YAML.
- A standalone CLI and Docker image that run a mock server from such a file.
