# Backlog

Proposed work for Rony.Net after 1.4.0. Nothing here is started or promised. The numbers are the ones used in the
1.1 planning, so they have gaps (1–5, 12 and 14 shipped in 1.1.0; 6, 7, 8 and 16 in 1.2.0; 9 and 13 in 1.3.0; 10, 11 and 15 are done for 1.4.0; see `CHANGELOG.md`).

To start an item: `/new-feature <item>` gives the checklist. When an item ships, remove it here and describe it in
`CHANGELOG.md`.

## Follow-ups to 1.4.0

Left out of item 15 on purpose; none is planned.

- Configuration files in YAML (needs a parser dependency, so it would live in `Rony.Net.Cli` only).
- A published Docker image (today the repo has a `Dockerfile` users build themselves).
- `rony run` options that override the address and port of the file.
- Faults in configuration files: truncated, corrupted, chunked and throttled responses, failing TLS handshakes.
