# Known Issues and Limitations

What Rony.Net 1.4.0 does not do, or does in a way that can surprise you. None of these breaks a normal test; they are listed
so you don't have to find them yourself. For problems with a known answer, see [Troubleshooting](Troubleshooting).

## Mock server
- **`RefuseConnections()` can reset a client that is connecting at that moment.** A client whose connection was completed by
  the operating system but not yet accepted by the server may see a reset instead of "connection refused". Call it when no
  client is in the middle of connecting. See [Simulating Failures](Simulating-Failures).
- **`WaitForConnectionAsync()` can return before your `ConnectionOpened` handler ran.** The connection is recorded first and
  the event is raised afterwards. If a test depends on the handler's effect, wait for that effect (for example with a
  `TaskCompletionSource` set in the handler). See [Connections and Push](Connections-and-Push).
- **`Log` can be called while the server holds an internal lock** (during `Start()`, `Stop()`, `RefuseConnections()` and
  `AcceptConnections()`). The callback must not wait for another thread that is calling one of them. See
  [Logging and Diagnostics](Logging-and-Diagnostics).
- **Received requests are kept until `Mock.Reset()`, connection records until the server is disposed.** That is what
  `Should()`, `ReceivedRequests` and `Connections` read. A server that runs for a long time, such as the standalone tool,
  grows with the traffic it receives.
- **No limit on buffered data unless you set one.** A client that never completes a message makes the server buffer what it
  sends. Set `MaxBufferedBytes` on the listener when the client is not your own code. Servers from a configuration file
  default to 16 MiB. See [Connections and Framing](Connections-and-Framing).
- **A regular expression given in code has no timeout.** A slow pattern delays all clients, because rules are matched one
  request at a time. Patterns from a configuration file time out after one second.

## TLS
- **A rejected client certificate does not fail at the same moment on every system.** Depending on the operating system and
  the TLS version, the client sees the rejection while connecting or on its first read or write. Wait for it on the server
  side (the log or `ConnectionFailed`). See [SSL and TLS](SSL-and-TLS).
- **Mutual TLS accepts any presented certificate unless you set `ClientCertificateValidator`.** The chain is not validated and
  revocation is not checked: test certificates are self-signed. A configuration file cannot set a validator.
- **`TestCertificate.CreateSelfSigned()` adds nothing to a certificate store, but the .NET runtime may keep the private key
  outside the process:** a temporary keychain file on macOS, a temporary key container on Windows. On macOS the key of such a
  certificate cannot be exported to a PFX file.
- **No TLS on Unix domain sockets or UDP.**

## Unix domain sockets and IPv6
- **`ResetAsync()` and `ResetConnection()` are a plain close on a Unix socket.** There is no RST; the client sees an end of
  stream or an error, depending on the platform.
- **A refused Unix-socket client gets a platform-dependent error,** not necessarily "connection refused", because the socket
  file is removed while connections are refused.
- **`Address` and `Port` mean nothing for a `UnixSocketServer`** (`IPAddress.None` and `0`); use `Path`.
- **In dual mode, IPv4 clients appear with IPv4-mapped addresses** such as `::ffff:127.0.0.1`.
- See [Servers](Servers).

## Record and replay
- **TCP and TLS only;** the proxy does not relay UDP.
- **Replay is order-dependent.** A reply that depends on earlier requests replays correctly only when the client sends its
  requests in the recorded order.
- **Recorded times are not replayed as delays.**
- **Calling `Stop()` or `Dispose()` on a `RecordingProxy` from its own `Log` callback hangs,** because it waits for the relay
  that is logging. Use `StopAsync()` there, or stop it from another place.
- **A recording and the proxy's log contain everything on the wire,** including credentials and tokens.
- See [Record and Replay](Record-and-Replay).

## Configuration files
- **A file cannot express everything the API can:** no response functions or predicates, no truncated, corrupted, chunked or
  throttled responses, no failing handshakes, no certificate validators, no custom framing. See
  [Not available in files](Configuration-Files#not-available-in-files).
- **A `udp` server binds its port when the file is loaded,** not when the server starts, as `new UdpServer(...)` does.
- **`tls.password` is plain text in the file.**
- **A slow regular expression delays all clients** until its one-second timeout.
- See [Configuration Files](Configuration-Files).

## Standalone server (`rony`)
- **For development and test networks only.** No limit on connections, idle time or handshake time, and memory grows with
  the traffic for as long as the tool runs. See [Limits and security](Standalone-Server#limits-and-security).
- **`rony run` takes the address and port from the file only;** there are no options to override them.
- **`rony validate` binds the port of a `udp` configuration,** so it reports "in use" while a server with that file runs.
- **A `unix` configuration needs `server.path`** when it is run with the tool.
- **`rony record` does not check the target before it starts.** An unreachable target shows up as a logged error per
  connection.
- **There is no published Docker image.** The repository has a `Dockerfile` to build one. In a container the configuration
  must listen on `0.0.0.0`.
- See [Standalone Server](Standalone-Server).

Found something that is not on this page? Please [open an issue](https://github.com/archofthings/Rony.Net/issues).
