# Logging and Diagnostics

When a test fails because the client got no response, or the wrong one, the question is always "what did the server
actually see and do?". Set `server.Log` and the server tells you, one line per event:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Log = output.WriteLine;          // xUnit's ITestOutputHelper; or Console.WriteLine, or your logger
server.Mock.Send("PING").Receive("PONG");
server.Start();
```

```
[Rony 10:15:02.091] listening on 127.0.0.1:50123
[Rony 10:15:02.097] #1 connected from 127.0.0.1:50124
[Rony 10:15:02.102] #1 received "PING" (matched "PING")
[Rony 10:15:02.103] #1 sent "PONG"
[Rony 10:15:02.110] #1 disconnected
[Rony 10:15:02.115] stopped
```

`#1` is the [connection](Connections-and-Push#inspecting-connections) id; UDP requests show the client's address instead.
With the [test framework packages](Test-Framework-Integration), the log goes to the test output without any setup.

## What gets logged

| Event | Example |
|---|---|
| Start and stop | `listening on 127.0.0.1:50123`, `stopped` |
| Connections | `#1 connected from 127.0.0.1:50124`, `#1 disconnected`; a TLS connection adds the protocol, server name and client certificate: `#1 connected from 127.0.0.1:50124 (Tls12, server name localhost, client certificate CN=my-client)` |
| Requests and the rule that matched | `#1 received "LIST" (matched /^LI/)`, `#1 received "LSIT" (unmatched)` |
| [Scenario state](Stateful-Scenarios) | `#1 received "LIST" (matched "LIST" in state "loggedIn")`, `#1 state "initial" -> "loggedIn"` |
| Responses | `#1 sent "PONG"`, `#1 sent "done" after 2000 ms`, `#1 no reply`, `#1 closing the connection` |
| Greetings and pushed messages | `#1 sent greeting "220 ready"`, `#1 pushed "NOTIFY"` |
| No response configured | `#1 no response configured: closing the connection` |
| Errors | `error: the response function for #1 threw FormatException: ...` |
| Failed connections | `connection from 127.0.0.1:50125 failed: AuthenticationException: ...` |

Binary payloads are shown as hex, for example `0x02 0x01 0x03`. Details a client controls (its server name, the subject of its
certificate, its address, exception messages) are escaped and cut at 256 characters, so a client cannot inject extra lines or
terminal sequences into the log. The standalone `rony` tool prints the same lines with the time in front
([Standalone Server](Standalone-Server)).

## Errors that are otherwise silent
The server never lets one bad request take it down, so some mistakes don't show up anywhere except the log:

- **A `Receive(...)` function throws.** The client gets an empty response (over TCP: nothing at all).
  The log shows the exception.
- **A `SendMatching(...)` predicate throws.** It counts as "doesn't match", and the log shows the exception.
- **A TLS handshake fails**, for example because the client doesn't trust the certificate or uses a protocol
  the server doesn't allow. The log shows the `AuthenticationException`.
- **A connection event handler throws.**

```csharp
server.Mock.Send("PRICE ACME").Receive(text => decimal.Parse(text.Split(' ')[2]).ToString());   // bug: no [2]
```
```
[Rony 10:15:02.102] #1 received "PRICE ACME" (matched "PRICE ACME")
[Rony 10:15:02.103] error: the response function for #1 threw IndexOutOfRangeException: Index was outside the bounds of the array.; sending an empty response
```

## Notes
- The callback is called from the server's background threads, possibly from several at once.
  `ITestOutputHelper`, NUnit's `TestContext.Out`, MSTest's `TestContext` and `Console` handle that.
- Exceptions thrown by the callback are ignored. xUnit, for example, throws when you write after the test finished,
  and that must not break the server.
- The callback can be called while the server holds an internal lock (during `Start()`, `Stop()`, `RefuseConnections()` and
  `AcceptConnections()`), so it must not wait for another thread that is calling one of them.
- To keep the lines, collect them: `server.Log = line => { lock (lines) lines.Add(line); };`

Runnable code: [`LoggingSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/LoggingSamples.cs)
