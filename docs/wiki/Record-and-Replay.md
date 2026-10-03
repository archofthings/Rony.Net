# Record and Replay

Instead of writing every rule by hand, record a conversation with the real server once and replay it in your tests.
A `RecordingProxy` sits between your client and the real server and records what goes through; `MockServer.Replay`
turns the recording into ordinary rules.

## Recording
```csharp
// 1. Record against the real server
using var proxy = new RecordingProxy("real.host", 5000) { Framing = MessageFraming.Delimiter("\n") };
proxy.Start();

// ... point the client at proxy.Port and run it ...
await proxy.WaitForConnectionsClosedAsync();   // the recording is complete
proxy.Recording.Save("login.rony.json");
```
The proxy is a plain TCP relay with its own sockets, listening on `127.0.0.1` and a free port by default
(`proxy.Port` after `Start()`). Bytes are forwarded unchanged and at once; the proxy only watches. Several connections
can go through it at the same time.

- **Framing.** `Framing` splits both directions into recorded messages, exactly as a `TcpServer` does. Use the framing of the
  protocol to get one recorded message per protocol message. The default (`MessageFraming.None`) records every read as
  one message, which depends on how the bytes happen to arrive. Set it before `Start()`. If the framing throws, or more than 16 MiB arrive
  without a complete message, the proxy records the pending bytes as one raw message and the rest of that direction
  unframed; the bytes are still relayed.
- **Waiting.** `WaitForConnectionsClosedAsync()` completes once at least one connection went through the proxy and all of
  them have ended (5 seconds by default, then `TimeoutException`), so you can save a complete recording without sleeping.
  `proxy.Recording` is always the same object, filled while traffic flows, and can be read or saved at any time.
- **Closing.** When one side closes or resets the connection, the proxy records who did it first and closes the other side.
  A side that only finishes sending (a half-close) is passed on as a half-close on plain TCP, so the reply still comes
  back; with TLS the connection is closed instead.
- **Failures.** If the real server cannot be reached, or a TLS handshake fails, the client is disconnected, the reason goes
  to `proxy.Log` (same idea as `server.Log`) and the proxy keeps accepting. The connection is still in the recording, with a
  close by the server.
- **Stopping.** `Stop()` / `StopAsync()` / `Dispose()` close every relayed connection and wait for them to end.
  Don't call `Stop()` or `Dispose()` from the `Log` callback (it waits for the relay that is logging); use `StopAsync()`
  from elsewhere.

### TLS
`Certificate` makes the proxy speak TLS to your client (like `TcpServerSsl`); `TargetTls` makes it speak TLS to the real
server, using the target host name for the handshake. `TargetCertificateValidation` is optional (default validation otherwise).
```csharp
using var proxy = new RecordingProxy("localhost", real.Port)
{
    Certificate = certificate,
    TargetTls = true,
    TargetCertificateValidation = (_, serverCertificate, _, _) => serverCertificate?.GetCertHashString() == certificate.GetCertHashString()
};
```

## The file format
The recording is JSON (version 1). Offsets (`at`) are milliseconds since the connection was accepted.
```json
{
  "version": 1,
  "connections": [
    {
      "id": 1,
      "messages": [
        { "from": "server", "at": 3, "text": "220 ready" },
        { "from": "client", "at": 12, "text": "LOGIN bob" },
        { "from": "server", "at": 15, "base64": "AAEC/w==" },
        { "from": "server", "at": 20, "closed": true }
      ]
    }
  ]
}
```
- A message body is written as `text` when it is valid UTF-8 without control characters other than CR, LF and tab, and as
  `base64` otherwise. `closed: true` is the close event: that side closed (or reset) the connection first. It is always the last
  message of a connection.
- The files are meant to be edited by hand: change a reply, delete an exchange, or add a message. `Recording.Parse` ignores
  unknown properties, so you can add notes such as `"note": "..."`. `at` is optional. A message needs exactly one of `text` or
  `base64` (none for a close event). Anything else throws a `FormatException` that names the connection and message.
- `new Recording()` is empty, and `Recording.Parse(json)` / `recording.ToJson()` work with strings instead of files
  (`Save` writes UTF-8 without a byte order mark, indented). The sample project replays this very recording.

## Replaying
```csharp
// 2. Replay in tests
using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
server.Replay(Recording.Load("login.rony.json"));
server.Start();
```
Use the same framing on the server as the proxy used. `Replay` may be called before or after `Start()`. It adds rules to
`server.Mock`; it does not reset them. Calling it again for the same requests (or with another greeting) throws `ArgumentException`,
as for any request configured twice: call `server.Mock.Reset()` first to replace the rules. When `Replay` throws partway,
the rules added before the exception stay; call `server.Mock.Reset()` to start over.

How the recording becomes rules, connection by connection and message by message:

1. **Greeting.** The server messages before the first client message become `OnConnect()`. A connection without a greeting is
   ignored. As for requests, identical greetings give one step; otherwise every greeting becomes a step, in recorded order,
   so the first connection gets the first greeting, the second the second, and so on.
2. **Exchanges.** Each client message and the server messages that follow it become `Send(request).Receive(reply)`.
   The same request seen again, in this or a later connection, adds the next reply of a [sequence](Response-Sequences) in recorded
   order. If a request always got the same reply, one step is enough.
3. **No reply.** A client message with no server message after it becomes `NoReply()`, or `Disconnect()` if the server closed
   the connection right then.
4. **Server closes after its reply.** The reply gets `AndDisconnect()`. A close by the client is ignored.
5. **Several server messages** for one request are all sent, in order, as separate frames (TCP only).
6. An empty request is skipped, because an empty `Send("")` would match every request.

Recorded times are not replayed as delays: add `.After(...)` yourself if a test needs them.

## From the command line
The `rony record` and `rony replay` commands do the same without code, see [Standalone Server](Standalone-Server#record-a-real-server-once-replay-it-offline).

## Limitations
- A recording and the proxy's log contain everything sent through the proxy, including credentials and tokens: review them
  before committing or sharing them.
- TCP and TLS only. The proxy does not relay UDP.
- Every request is matched on its own. A protocol whose reply depends on earlier requests replays correctly only when the client
  sends its requests in the recorded order. For anything smarter, edit the rules afterwards or use [stateful scenarios](Stateful-Scenarios).
- A recording made without the protocol's framing replays only if the client sends the bytes in the same pieces. Without
  framing, a reply may also be recorded ahead of the request it answers when the client pipelines data.

Runnable code: [`RecordAndReplaySamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/RecordAndReplaySamples.cs)
