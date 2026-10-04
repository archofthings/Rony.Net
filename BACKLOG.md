# Backlog

Proposed work for Rony.Net after 1.4.0. Nothing here is started or promised. The numbers are the ones used in the
1.1 planning, so they have gaps (1–5, 12 and 14 shipped in 1.1.0; 6, 7, 8 and 16 in 1.2.0; 9 and 13 in 1.3.0; 10, 11 and 15 in 1.4.0; see `CHANGELOG.md`).

To start an item: `/new-feature <item>` gives the checklist. When an item ships, remove it here and describe it in
`CHANGELOG.md`. An improvement that is fixed is also removed from `docs/wiki/Known-Issues.md`.

## Plan

Related items are grouped so that each version has one theme. The version numbers are a proposal, not a promise.

| Version | Theme | Items |
|---|---|---|
| 1.4.1 | Fixes to the mock server and the tool; no new API | I1 (I2–I6 are fixed, see `CHANGELOG.md`) |
| 1.5.0 | The tool: verify, operate, deploy | 17, 18, 19, 20, 34, I7–I10 |
| 1.6.0 | Files and proxying: say more in a file, mock only a part | 21, 22, 23, 24, 25, 29, 32 |
| 1.7.0 | Network conditions: resilience testing | 30, 31 |
| 1.8.0 | Binary, long-lived and well-known protocols | 26, 27, 28, 33 |

Order inside the plan: 1.4.1 first (small, lowers the Known Issues list), then 1.5.0 (closes the biggest gap between the
library and the tool). 1.6.0, 1.7.0 and 1.8.0 do not depend on each other and can swap, except that item 31 builds on
the pass-through of item 23; within 1.6.0, item 21 comes before 22 and 24 because they add to the file format it
extends, and 23 before 29.

Items 29 to 34 come from a look at what comparable tools offer (mountebank, Toxiproxy, MockServer's TCP chaos profile,
Mockly) in October 2026; they are the features those tools have for raw TCP that Rony.Net lacks.

## 1.4.1: fixes

No new public API; patch version.

- **I1. `RefuseConnections()` and a client that is connecting.** A client whose connection the operating system completed
  but the server has not accepted yet can see a reset. It should see "connection refused" or be served.

## 1.5.0: the tool can be verified, operated and deployed

Today a test that is not written in .NET can talk to `rony run` but cannot ask what it received, and a long run grows
without limit.

- **17. Verification for the tool.** A `--journal <file>` option that appends every received request as a JSON line,
  and a control endpoint on a separate loopback port to list received requests, reset them, read or set the scenario
  state and switch a named set of rules on or off in one step. Lets a test in any language assert against the mock and
  steer it.
- **18. Hot reload: `rony run --watch`.** Reloads the rules when the configuration file changes, without dropping open
  connections; a file with a mistake is reported and the old rules stay.
- **19. `rony run` options that override the file:** `--port` and `--address`, so one file serves on different ports
  (needs a way to build the listener from a file with overrides, including UDP, which binds when it is created).
- **20. A published Docker image,** built and pushed by the release workflow (registry, tags and permissions to be
  decided), with the `docker compose` example of the wiki run in CI.
- **34. A Testcontainers module** (`Rony.Net.Testcontainers`): starts the published image of item 20 from a test, waits
  until it listens and gives the mapped port, for stacks that run their dependencies as containers.
- **I7. Memory growth.** The tool keeps every received request and connection record for its whole run, and `record`
  keeps the whole recording in memory. A cap ("keep the last N") makes long runs safe; it fits with item 17, which
  moves the full history to a file.
- **I8. `rony validate` binds the port of a `udp` configuration,** because `UdpServer` binds in its constructor. A
  parse-only path validates without touching the network; item 19 needs the same split of parsing and building.
- **I9. A `unix` configuration without `server.path` cannot be run by the tool,** because the generated path is not
  readable from outside the server. Expose it and print it.
- **I10. Tests:** the 16 MiB default of `server.maxBufferedBytes` is only tested through the parsing of the property.

## 1.6.0: files and proxying

What a file can say, and mocking only a part of a real service.

- **21. Several listeners in one file:** a `servers` array, so one configuration and one `rony run` serve, for example,
  the TCP and the UDP port of the same device, sharing rules and state where asked.
- **22. Faults in configuration files:** truncated, corrupted, chunked and throttled responses, and failing TLS
  handshakes, which today need code.
- **23. Pass-through for unmatched requests:** `OnUnmatched().ForwardTo(host, port)` (and `onUnmatched: { "forwardTo": … }`
  in a file) answers some requests from rules and sends the rest to a real server. Builds on `RecordingProxy`.
- **24. Recording to configuration file:** `rony convert <recording.json>` and an API for it, so a recording becomes an
  editable configuration instead of being rewritten by hand.
- **25. Configuration files in YAML,** in `Rony.Net.Cli` only, because it needs a parser dependency and the core has
  none.

- **29. Learn mode: record on the first miss.** With pass-through (item 23) an unmatched request is forwarded once and
  its answer is kept as a rule, so the next identical request is answered without the real server; the learned rules
  can be saved as a configuration file (item 24). What mountebank calls `proxyOnce`.
- **32. Replies from a data file:** a rule looks a captured value up in a CSV or JSON file and uses the row in its reply
  (`"lookup"` in a file, a small API in code), so one rule answers for many accounts, devices or products.

## 1.7.0: network conditions

Today faults belong to a rule (`After`, `InChunks`, `Throttled`, `Truncated`, `ResetConnection`). Resilience tests want
them on the whole server, switchable in the middle of a test.

- **30. Network conditions on the server:** `server.Network` with latency (and jitter), bandwidth, slicing into small
  pieces, closing after N bytes, slow close, never closing (a half-open peer), accepting but answering nothing ("down"),
  and not reading at all, so the client's sends block and its write timeout is tested. Each can apply to every
  connection or with a probability (seeded, so a test is repeatable), and can be changed while the server runs; also in
  configuration files and through the control endpoint of item 17. The set matches the "toxics" of Toxiproxy.
- **31. A fault-injecting proxy:** the same conditions between a client and a real server, for example a database in a
  container: `new FaultProxy(targetHost, targetPort)` relays unchanged until a test turns a condition on. An
  in-process alternative to Toxiproxy for .NET tests, and `rony proxy` for everything else. Builds on item 23.

## 1.8.0: binary, long-lived and well-known protocols

- **26. Byte patterns:** matching with wildcards and masks, such as `Send(BytePattern.Parse("02 ?? ?? 10 *"))`, and replies
  that copy bytes from the request (echo a sequence number); also in configuration files. Binary protocols need a
  hand-written predicate today.
- **27. Scheduled server messages:** `OnConnect().Every(TimeSpan).Send("PING")` for heartbeats and keep-alives, stopping
  with the connection; also in configuration files. Today this needs a loop in the test that calls `BroadcastAsync`.
- **28. Windows named pipes:** a `NamedPipeServer` as the Windows counterpart of `UnixSocketServer`, with the same
  feature set and the same limits (no TLS, no RST).

- **33. Protocol presets** in a separate package (`Rony.Net.Protocols`), so the core stays small: framing and matching
  helpers for protocols people ask a mock for, such as Redis (RESP), SMTP and other line protocols, MLLP (HL7),
  Modbus TCP and ISO 8583, each with a wiki recipe.

## Ideas, not planned yet

- A scripted mock client: the reverse role, connecting to a server under test and playing a conversation, with the
  same matching and assertions.
- A transcript of a whole conversation as one value, for snapshot tests.

## Not planned

- HTTP and WebSocket support: mature tools exist for them, and the value of this library is in raw TCP, TLS and UDP.
- `dotnet test` twice took minutes of wall-clock time on macOS although its tests finished in seconds (cause unknown).
  If it happens again, capture `--blame-hang-timeout` and `--diag` output.
