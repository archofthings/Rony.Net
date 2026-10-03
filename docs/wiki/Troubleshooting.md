# Troubleshooting

### "Address already in use" / `SocketException` on start
Another server or program is using the port. Use port `0` and read `server.Port` after `Start()`; see
[Ports and Lifecycle](Ports-and-Lifecycle). Also make sure every server is disposed (`using var server = ...`).
`UdpServer` binds when it is created, so for UDP the error comes from the constructor.

### A Unix socket server fails to start
- `SocketException` "Address already in use": the socket file already exists, for example left behind by a crashed run. The
  server never deletes a file it did not create; delete it yourself or use a new path.
- `ArgumentOutOfRangeException`: the path is longer than about 104 bytes. Use a short path such as `/tmp/my.sock`.
- `PlatformNotSupportedException`: Unix domain sockets are not available on this system.
See [Unix domain sockets](Servers#unix-domain-sockets).

### My client hangs waiting for the end of the response
Since 1.0, TCP connections stay open after a response. A client that reads until the server closes the connection
will wait forever. Either:
- set `KeepAlive = false` on the server: `new TcpServer(0) { KeepAlive = false }`, or
- add `.AndDisconnect()` to the responses after which the server should close.

### Requests don't match even though the text looks right
- The client may send a line ending or terminator, such as `"PING\r\n"`. Configure
  [framing](Connections-and-Framing) (`MessageFraming.Delimiter("\r\n")`) so it is removed before matching,
  or match on the full text.
- Check what actually arrived: `server.ReceivedRequests` or a failing `server.Should().HaveReceived(...)` lists every request. Binary data is shown as hex.
- Two requests sent quickly can arrive together and be treated as one. Use framing so they are split correctly.

### Two responses come back as one
TCP doesn't keep message boundaries, so the client may receive two responses in one read. Use framing on the server
and read by message on the client: up to the delimiter, or by length.

### My client never gets a response
- **Turn on the log first:** `server.Log = output.WriteLine;` (or `Console.WriteLine`). It shows every request, the
  rule it matched, the response and any error. See [Logging and Diagnostics](Logging-and-Diagnostics).
- Is the request matched? `server.Mock.UnmatchedRequests` lists requests without a response. Over TCP, an unmatched request closes the connection.
- Did a `Receive(...)` function throw? The response is then empty, and over TCP nothing is sent. The log shows the exception.
- Is the request matched in the right [state](Stateful-Scenarios)? The log shows the state of every request.
- Is a delimiter expected? With `MessageFraming.Delimiter`, it is appended to responses for you. Without framing, include it in the response yourself.
- Was the server started? Call `server.Start()`.

### The TLS handshake fails
The [log](Logging-and-Diagnostics) shows the server-side error, for example `connection from 127.0.0.1:50125 failed: AuthenticationException: ...`.
- When you pass a certificate object, it must have a private key (`certificate.HasPrivateKey`). `TestCertificate.CreateSelfSigned()`
  does this for you; for your own, see [SSL and TLS](SSL-and-TLS).
- When you pass a certificate *name*, a certificate with that subject name and a readable private key must exist in the
  `CurrentUser` or `LocalMachine` "My" store. If none is found, the server can't complete the handshake and closes the connection.
  Passing a certificate object avoids this.
- The client must trust the certificate; see [Trusting the certificate](SSL-and-TLS#trusting-the-certificate-in-your-client).
- The host name the client validates (`AuthenticateAsClientAsync("localhost")`) must match the certificate.

### Mutual TLS: a client is rejected
With `RequireClientCertificate = true` a client that sends no certificate, or one that your `ClientCertificateValidator` refuses,
fails the handshake. The [log](Logging-and-Diagnostics) shows why ("the client sent no certificate" or the validator's decision) and `ConnectionFailed` is raised.
The client may notice only on its first read or write, depending on the system and the TLS version: wait on the server side
(see [Mutual TLS](SSL-and-TLS#mutual-tls-client-certificates) and [Known Issues](Known-Issues#tls)).

### A connection is closed although the client did nothing wrong
- With `MaxBufferedBytes` set (servers from a configuration file: 16 MiB), a connection that buffers more bytes than the limit
  without a complete message is closed and logged (`InvalidDataException`). Check the [framing](Connections-and-Framing#limiting-the-buffered-bytes): a delimiter that never arrives is the usual cause.
- A length-prefixed message with an invalid length (smaller than the prefix, or negative) closes that connection too.

### `MockServer.FromFile` / `FromJson` throws `FormatException`
The message starts with the place of the mistake, such as `rules[1]: unknown property "replys"`. Unknown properties are errors on purpose, so typos
are found. See the [error list](Configuration-Files#errors). The same message is printed by `rony run` and `rony validate`.

### The `rony` tool exits
Exit code `1` is a runtime failure (for example `Error: Address already in use` for a port that is taken, or a Unix socket path that is too long),
`2` a usage error or an invalid or missing file (the message names the file and the place). Details in [Standalone Server](Standalone-Server#exit-codes).

### `Receive(x => x)` doesn't compile ("The call is ambiguous")
The lambda fits both the text and the byte overloads. Give it a type: `Receive((byte[] x) => x)` or `Receive((string x) => x)`.

### `Times` is ambiguous with Moq
Both libraries define `Times`. Use `Rony.Net.Times.Once()`, or add `using Times = Rony.Net.Times;` in files that only verify Rony.Net requests.

### An assertion fails, but the client did send the request
The request may still be on its way, for example when your code sends in the background. Use
[`await server.Mock.WaitForRequestAsync(...)`](Waiting-for-Requests) before verifying.

### Tests are slow
- Use short timeouts in the client under test.
- Don't wait for real timeouts with `NoReply()` longer than you need to; make the client's timeout configurable.
- Replace `Thread.Sleep` with [`WaitForRequestAsync`](Waiting-for-Requests).

### Still stuck?
Check the [known issues and limitations](Known-Issues) first.

[Open an issue](https://github.com/archofthings/Rony.Net/issues) with a small test that shows the problem.
