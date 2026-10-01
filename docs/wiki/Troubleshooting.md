# Troubleshooting

### "Address already in use" / `SocketException` on start
Another server or program is using the port. Use port `0` and read `server.Port` after `Start()`; see
[Ports and Lifecycle](Ports-and-Lifecycle). Also make sure every server is disposed (`using var server = ...`).
`UdpServer` binds when it is created, so for UDP the error comes from the constructor.

### My client hangs waiting for the end of the response
Since 1.0, TCP connections stay open after a response. A client that reads until the server closes the connection
will wait forever. Either:
- set `KeepAlive = false` on the server: `new TcpServer(0) { KeepAlive = false }`, or
- add `.AndDisconnect()` to the responses after which the server should close.

### Requests don't match even though the text looks right
- The client may send a line ending or terminator, such as `"PING\r\n"`. Configure
  [framing](Connections-and-Framing) (`MessageFraming.Delimiter("\r\n")`) so it is removed before matching,
  or match on the full text.
- Check what actually arrived: `server.ReceivedRequests` or a failing `Verify(...)` lists every request. Binary data is shown as hex.
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
- When you pass a certificate object, it must have a private key (`certificate.HasPrivateKey`). Certificates created
  in code need the export/re-import step shown in [SSL and TLS](SSL-and-TLS), especially on Windows.
- When you pass a certificate *name*, a certificate with that subject name and a readable private key must exist in the
  `CurrentUser` or `LocalMachine` "My" store. If none is found, the server can't complete the handshake and closes the connection.
  Passing a certificate object avoids this.
- The client must trust the certificate; see [Trusting the certificate](SSL-and-TLS#trusting-the-certificate-in-your-client).
- The host name the client validates (`AuthenticateAsClientAsync("localhost")`) must match the certificate.

### `Receive(x => x)` doesn't compile ("The call is ambiguous")
The lambda fits both the text and the byte overloads. Give it a type: `Receive((byte[] x) => x)` or `Receive((string x) => x)`.

### `Times` is ambiguous with Moq
Both libraries define `Times`. Use `Rony.Net.Times.Once()`, or add `using Times = Rony.Net.Times;` in files that only verify Rony.Net requests.

### `Verify` fails, but the client did send the request
The request may still be on its way, for example when your code sends in the background. Use
[`await server.Mock.WaitForRequestAsync(...)`](Waiting-for-Requests) before verifying.

### Tests are slow
- Use short timeouts in the client under test.
- Don't wait for real timeouts with `NoReply()` longer than you need to; make the client's timeout configurable.
- Replace `Thread.Sleep` with [`WaitForRequestAsync`](Waiting-for-Requests).

### Still stuck?
[Open an issue](https://github.com/archofthings/Rony.Net/issues) with a small test that shows the problem.
