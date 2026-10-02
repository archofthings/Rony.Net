# API Reference

Every public type in Rony.Net 1.1. The package includes XML documentation, so IntelliSense shows the same descriptions in your editor.

## `Rony.Net.MockServer`
The mock server. Wraps a listener and answers requests with the responses configured on `Mock`.

| Member | Description |
|---|---|
| `MockServer(IListener listener)` | Creates a server on the given listener |
| `void Start()` | Starts listening. Repeated calls do nothing. |
| `void Stop()` | Stops listening, closes connections and cancels delayed responses. Repeated calls do nothing. |
| `void Dispose()` | Stops the server and releases the listener |
| `Task StartAsync(CancellationToken cancellationToken = default)` | Starts listening; completes once the server is listening. A cancelled token cancels the task and the server is not started. |
| `void RefuseConnections()` | New clients get "connection refused"; connections the server has accepted keep working (a client not accepted yet may be reset). Throws `InvalidOperationException` when not started, `NotSupportedException` without `IFaultInjectionListener` |
| `void AcceptConnections()` | Listens again on the same port; does nothing when not refusing. Throws `SocketException` if the port cannot be bound again |
| `Task StopAsync()` | Like `Stop()`, then waits until the server's background work has ended; afterwards no callback of yours runs until the next start. Do not await it from inside a callback. |
| `ValueTask DisposeAsync()` | `StopAsync()`, then the same cleanup as `Dispose()` (`await using`) |
| `bool Active` | Whether the server is started |
| `IPAddress Address` | The listening address |
| `int Port` | The listening port (the assigned one when created with port `0`) |
| `RequestHandler Mock` | Configuration, recording and verification |
| `IReadOnlyList<ReceivedRequest> ReceivedRequests` | Shortcut for `Mock.ReceivedRequests` |
| `Action<string> Log` | Receives a [log](Logging-and-Diagnostics) line for everything the server does |
| `MockServerAssertions Should()` | [Fluent assertions](Verifying-Requests#fluent-assertions) |
| `void Replay(Recording recording)` | Adds rules that answer like the recorded server; see [Record and Replay](Record-and-Replay). Throws `ArgumentException` for requests that already have a rule |

**Connections** (TCP; on other listeners `Connections` is empty and the methods throw `NotSupportedException`). See [Connections and Push](Connections-and-Push).

| Member | Description |
|---|---|
| `IReadOnlyList<ClientConnection> Connections` | Every accepted connection, open or closed, oldest first |
| `IReadOnlyList<ClientConnection> OpenConnections` | The connections still open |
| `event EventHandler<ClientConnection> ConnectionOpened` | A client connected |
| `event EventHandler<ClientConnection> ConnectionClosed` | A connection closed, by either side |
| `Task<int> BroadcastAsync(string or byte[] message)` | Pushes a message to every open connection; returns how many it reached |
| `Task<ClientConnection> WaitForConnectionAsync(TimeSpan? timeout)` | Waits for the first connection |
| `Task<IReadOnlyList<ClientConnection>> WaitForConnectionsAsync(int count, TimeSpan? timeout)` | Waits until `count` connections were accepted |
| `Task WaitForAllConnectionsClosedAsync(TimeSpan? timeout, CancellationToken)` | Waits until no accepted connection is open (TCP only) |
| `void VerifyConnections(Times times)` | How many connections were accepted |

## `Rony.Handlers.RequestHandler`
Available as `server.Mock`.

**Choosing the request**

| Member | Matches |
|---|---|
| `Send(string request)` | This exact text. `""` matches any request. |
| `Send(byte[] request)` | These exact bytes. Empty matches any request. |
| `Send(Regex pattern)` | Requests whose text matches the pattern |
| `SendMatching(Func<string, bool> predicate)` | Requests whose text satisfies the predicate |
| `SendMatchingBytes(Func<byte[], bool> predicate)` | Requests whose bytes satisfy the predicate |
| `SendJson(Func<JsonValue, bool> predicate)` | Requests that are valid JSON and satisfy the predicate; other requests don't match |
| `InState(string state).Send...(...)` | Any of the above, only in that [scenario state](Stateful-Scenarios) |
| `OnConnect()` | A new TCP connection: the response is a [greeting](Connections-and-Push#greetings-talk-first) |
| `OnUnmatched()` | Requests no other rule matches; they stay [unmatched](Request-Matching#unmatched-requests) |

**Choosing the response** (each returns a `ResponseBuilder`)

| Member | Effect |
|---|---|
| `Receive(string response)` | Responds with this text |
| `Receive(byte[] response)` | Responds with these bytes |
| `Receive(Func<string, string> func)` | Responds with `func(request text)` |
| `Receive(Func<byte[], byte[]> func)` | Responds with `func(request bytes)` |
| `ReceiveMatch(Func<Match, string> func)` | Responds with `func(regex match)`; only after `Send(Regex)` (otherwise `InvalidOperationException`) |
| `Disconnect()` | Closes the TCP connection without replying |
| `ResetConnection()` | Aborts the TCP connection with a reset (RST) without replying |
| `NoReply()` | Never replies |

**Matching and state**

| Member | Description |
|---|---|
| `byte[] Match(string or byte[] request)` | Runs the server's lookup and returns the response (also records the request) |
| `IReadOnlyDictionary<byte[], Config> Configs` | Exact-request configurations without a state, keyed by request bytes |
| `string State` | The server-wide [scenario state](Stateful-Scenarios); settable |
| `StateScope StateScope` | `Server` (default): one state; `Connection`: a state per connection (UDP: per client address) |
| `const string InitialState` | `"initial"`, the state before any `GoTo(...)` |
| `void Reset()` | Removes every configuration and recorded request, and returns to `InitialState` |

**Verification**

| Member | Description |
|---|---|
| `IReadOnlyList<ReceivedRequest> ReceivedRequests` | Every request, oldest first |
| `IReadOnlyList<ReceivedRequest> UnmatchedRequests` | Requests with no configured response |
| `void Verify(string or byte[] request)` | At least once |
| `void Verify(string or byte[] request, Times times)` | The given number of times |
| `void Verify(Func<ReceivedRequest, bool> predicate[, Times times])` | Requests satisfying the predicate |
| `void VerifyAllRequestsMatched()` | Every request had a configured response |
| `void VerifyInOrder(params string[] / byte[][] / Func<ReceivedRequest, bool>[])` | The requests arrived in this order; others may be in between |
| `bool FailOnUnmatched` | Once an unmatched request arrives, every `Verify...` and `WaitFor...` throws |
| `void ClearReceivedRequests()` | Forgets recorded requests and keeps the configuration |

**Waiting** (the timeout defaults to 5 seconds; each method also takes an optional `CancellationToken`)

| Member | Completes when |
|---|---|
| `Task<ReceivedRequest> WaitForRequestAsync(TimeSpan? timeout)` | Any request has been received |
| `Task<ReceivedRequest> WaitForRequestAsync(string or byte[] request, TimeSpan? timeout)` | That request has been received |
| `Task<ReceivedRequest> WaitForRequestAsync(Func<ReceivedRequest, bool> predicate, TimeSpan? timeout)` | A request satisfying the predicate has been received |
| `Task<IReadOnlyList<ReceivedRequest>> WaitForRequestsAsync(int count, TimeSpan? timeout)` | At least `count` requests have been received |

Verification failures throw `MockVerificationException`, and waits that time out throw `TimeoutException`.

## `Rony.Handlers.ResponseBuilder`
Returned by `Receive(...)`, `Disconnect()`, `ResetConnection()` and `NoReply()`.

| Member | Effect |
|---|---|
| `Then(string / byte[] / Func<string, string> / Func<byte[], byte[]>)` | Adds the next response in the sequence |
| `ThenMatch(Func<Match, string> func)` | Adds the next response, built from the regex match; only after `Send(Regex)` |
| `ThenDisconnect()` | Next time: close without replying |
| `ThenNoReply()` | Next time: no reply |
| `After(TimeSpan delay)` | Delays the previous response |
| `AndDisconnect()` | Closes the connection after the previous response |
| `ThenResetConnection()` | Next time: reset the connection (RST) without replying |
| `AndResetConnection()` | Resets the connection after the previous response was written (the client may not see it) |
| `Truncated(int byteCount)` | Sends only the first `byteCount` bytes of the previous response, as it goes on the wire (after framing) |
| `Corrupted(Func<byte[], byte[]> corrupt)` | Changes the bytes of the previous response, as it goes on the wire; gets a copy |
| `InChunks(int chunkSize, TimeSpan delay = default)` | Sends the previous response, as it goes on the wire, in pieces of `chunkSize` bytes with `delay` between them; the last call of `InChunks`/`Throttled` wins |
| `Throttled(int bytesPerSecond)` | Sends the previous response at about this rate (ten pieces a second; one byte at a time under 10 bytes/s) |
| `GoTo(string state)` | Moves the [scenario](Stateful-Scenarios) to `state` once the previous response is used |

The last response in a sequence repeats once the sequence ends.

## Listeners (`Rony.Listeners`)

| Type | Constructors |
|---|---|
| `TcpServer` | `(int port = 3000)`, `(string address, int port = 3000)`, `(IPAddress address, int port = 3000)` |
| `TcpServerSsl` | `(int port, X509Certificate certificate, SslProtocols protocol)`, plus `string address` / `IPAddress address` overloads; the same three with `string certificateName` instead of a certificate |
| `UdpServer` | `(int port = 3000)`, `(string address, int port = 3000)`, `(IPEndPoint localEndPoint)` |

`TcpServer` and `TcpServerSsl` derive from **`TcpServerBase`**:

| Member | Description |
|---|---|
| `IMessageFraming Framing` | How the stream is split into messages. Default: `MessageFraming.None`. Set before `Start()`. |
| `bool KeepAlive` | Keep connections open after a response. Default: `true`. |
| `bool FailHandshake` | (`TcpServerSsl` only) Every new TLS handshake fails. Can change while running. |
| `void RefuseConnections()` | Stops accepting new connections (clients get "connection refused"); accepted connections keep working |
| `void AcceptConnections()` | Listens again on the same port; does nothing when not refusing. Throws `SocketException` if the port cannot be bound again |
| `byte[] Frame(byte[] message)` | Frames a message with `Framing` |
| `Task SendRawAsync(byte[] data, object sender)` | Writes bytes to a connection as they are, without framing |
| `Task SendRawAsync(byte[] data, object sender, int chunkSize, TimeSpan delay, CancellationToken cancellationToken)` | Writes bytes as they are in pieces, `delay` apart, holding the connection's write lock so nothing is written in between |
| `Task ResetAsync(object sender)` | Aborts a connection with a TCP reset (RST) |
| `protected abstract Task<Stream> OpenStreamAsync(TcpClient client)` | Prepares the stream for a new connection |
| `protected virtual bool HasPendingData(Stream stream)` | Whether more data can be read right away |

## Framing (`Rony.Listeners.MessageFraming`, `Rony.Interfaces.IMessageFraming`)

| Member | Description |
|---|---|
| `MessageFraming.None` | One burst is one message (default) |
| `MessageFraming.Delimiter(string or byte[] delimiter)` | Messages end with the delimiter |
| `MessageFraming.LengthPrefix(int prefixLength = 4, bool bigEndian = true)` | Messages start with a 1, 2 or 4-byte length |
| `MessageFraming.LengthPrefix(int prefixLength, bool bigEndian, bool includesPrefix)` | Like the above; with `includesPrefix: true` the length counts the prefix too. A length smaller than the prefix closes that connection |
| `MessageFraming.FixedLength(int length, byte padding = 0)` | Every `length` bytes are one message; shorter responses are padded on the right |
| `MessageFraming.StartEnd(byte start, byte end)` | Messages sit between a start and an end byte; no escaping |
| `MessageFraming.StxEtx` | `StartEnd(0x02, 0x03)` |
| `IMessageFraming.Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)` | Extracts complete messages |
| `IMessageFraming.Encode(byte[] response)` | Frames a response |

## `Rony.Net.Times`
`Never()`, `Once()`, `AtLeastOnce()`, `Exactly(n)`, `AtLeast(n)`, `AtMost(n)`, `Between(min, max)`, plus `Matches(count)`, `Min` and `Max`.

## `Rony.Models.ReceivedRequest`
`Body` (bytes), `BodyString` (UTF-8 text), `RemoteEndPoint`, `Timestamp`, `Matched`, `ConnectionId` (TCP).

## `Rony.Models.ClientConnection`
A TCP connection the server accepted; see [Connections and Push](Connections-and-Push).

| Member | Description |
|---|---|
| `int Id` | 1, 2, ... in the order connections were accepted |
| `EndPoint RemoteEndPoint` | The client's address |
| `DateTimeOffset ConnectedAt`, `DateTimeOffset? ClosedAt` | When it was accepted and closed |
| `bool IsOpen` | Whether it is still open |
| `string State` | Its scenario state (`StateScope.Connection`), or the server-wide state |
| `IReadOnlyList<ReceivedRequest> ReceivedRequests` | The requests received on it |
| `Task SendAsync(string or byte[] message)` | Pushes a message, framed like a response |
| `Task CloseAsync()` | Closes it from the server side |
| `Task ResetAsync()` | Aborts it with a TCP reset (RST); throws `NotSupportedException` without `IFaultInjectionListener` |
| `Task WaitForCloseAsync(TimeSpan? timeout)` | Waits until it is closed |
| `ClientConnectionAssertions Should()` | Fluent assertions on this connection |

## `Rony.Net.MockServerAssertions`
Returned by `server.Should()`; every method returns the assertions again, and `And` reads well between them.
`HaveReceived(request or predicate[, Times])`, `NotHaveReceived(...)`, `HaveReceivedInOrder(...)`,
`HaveNoUnmatchedRequests()`, `HaveAcceptedConnections(Times)`, `HaveNoOpenConnections()`, `BeInState(string)`.
See [Fluent assertions](Verifying-Requests#fluent-assertions).

## `Rony.Net.ClientConnectionAssertions`
Returned by `connection.Should()`; every method returns the assertions again, and `And` reads well between them.
`HaveReceived(request or predicate[, Times])`, `NotHaveReceived(...)` and `HaveReceivedInOrder(...)` count only
requests received on that connection; `BeInState(string)`, `BeOpen()`, `BeClosed()`.
See [Assertions on one connection](Verifying-Requests#assertions-on-one-connection).

## `Rony.Models.JsonValue`, `JsonKind`
A small immutable JSON value parsed by the library (no dependency), passed to `SendJson(...)` predicates.
`JsonValue.Parse(string)` (throws `FormatException`) and `TryParse(string, out JsonValue)`; `Kind` (`Undefined`, `Null`, `Boolean`,
`Number`, `String`, `Array`, `Object`), `Exists`, indexers `[string name]` and `[int index]` (never throw; a missing part is
`Undefined`), `Count`, `Items`, `Properties`, `AsString()`, `AsNumber()`, `AsBoolean()` (null for another kind) and `ToString()`
(compact JSON). See [JSON requests](Request-Matching#json-requests).

## `Rony.Net.RecordingProxy`, `Rony.Models.Recording`
A TCP/TLS relay that records the traffic between a client and a real server (no UDP); see [Record and Replay](Record-and-Replay).

| Member | Description |
|---|---|
| `RecordingProxy(string targetHost, int targetPort, int port = 0)` | Listens on `127.0.0.1`; port `0` picks a free port on start |
| `RecordingProxy(IPAddress address, int port, string targetHost, int targetPort)` | Listens on the given address |
| `Address`, `Port`, `bool Active` | Where it listens (`Port` is the assigned one after `Start()`) and whether it is started |
| `IMessageFraming Framing` | Splits both directions into recorded messages; default `MessageFraming.None`. Set before `Start()` |
| `X509Certificate Certificate` | Speak TLS to the client with this certificate; default plain TCP |
| `bool TargetTls`, `RemoteCertificateValidationCallback TargetCertificateValidation` | Speak TLS to the real server; optional certificate validation |
| `Action<string> Log` | Receives a line per connection and relayed message; exceptions from it are ignored |
| `Recording Recording` | The live recording; thread-safe to read or save at any time |
| `void Start()`, `void Stop()`, `Task StopAsync()`, `Dispose()`, `DisposeAsync()` | Lifecycle; stopping closes every relayed connection and waits for it to end |
| `Task WaitForConnectionsClosedAsync(TimeSpan? timeout = null, CancellationToken = default)` | Completes once a connection was relayed and all have ended; `TimeoutException` after 5 s by default |

`Recording`: `new Recording()`, `IReadOnlyList<RecordedConnection> Connections`, `ToJson()`, `Parse(string)` (throws `FormatException`),
`Save(string path)`, `Load(string path)`. `RecordedConnection`: `Id`, `IReadOnlyList<RecordedMessage> Messages`.
`RecordedMessage`: `RecordedSource Source` (`Client` or `Server`), `Body`, `BodyString`, `Offset`, `IsClose`.

## `Rony.Net.StateScope`
`Server` (one scenario state for the server) or `Connection` (one per connection).

## `Rony.Models.Config`
One exact-request configuration: `CallCount`, and `GetResponse(string or byte[])`, which returns the next response and moves the sequence forward.

## `Rony.Interfaces.IListener`, `IConnectionListener`, `IFaultInjectionListener`
The transport contract, its extension for transports with connections, and the optional extension for failure
simulation (`ResetAsync`, `Frame`, `SendRawAsync` (also the chunked overload), `RefuseConnections`, `AcceptConnections`); see [Custom Listeners](Custom-Listeners).
`TcpServer` and `TcpServerSsl` implement `IConnectionListener` and `IFaultInjectionListener`.

## Test framework packages
`Rony.Net.Xunit` (xUnit v2), `Rony.Net.Xunit.v3` (xUnit v3, same types and namespace), `Rony.Net.NUnit` and `Rony.Net.MSTest`: a `MockServerTest` base class (`Server`,
`VerifyAllRequestsMatchedAfterTest`, `CreateListener()`) and `LogTo(...)` / `LogToTestContext()` extensions.
See [Test Framework Integration](Test-Framework-Integration).

## `Rony.Models.Message`
A request as delivered by a listener: `Body`, `BodyString`, `Sender`, `RemoteEndPoint`.

## `Rony.Convertor`
UTF-8 extension methods: `string.GetBytes()` and `byte[].GetString()`.

## Exceptions

| Exception | Thrown by |
|---|---|
| `MockVerificationException` | `Verify...(...)`, `VerifyInOrder(...)`, `VerifyAllRequestsMatched()`, `VerifyConnections(...)`, `Should()` assertions; waits with `FailOnUnmatched` |
| `TimeoutException` | `WaitForRequestAsync(...)`, `WaitForRequestsAsync(...)`, `WaitForConnection(s)Async(...)`, `WaitForCloseAsync(...)` |
| `ArgumentException` | Configuring the same exact request twice in the same state, or `OnConnect()`/`OnUnmatched()` twice; an empty delimiter |
| `InvalidOperationException` | `Receive(...)` without `Send(...)`; `ReceiveMatch(...)` / `ThenMatch(...)` on a rule that was not started with `Send(Regex)`; a response too long for its length prefix; pushing to a closed connection |
| `NotSupportedException` | Connection members on a listener without connections, such as `UdpServer` |
| `FormatException` | `JsonValue.Parse(...)` with invalid JSON |
| `ArgumentOutOfRangeException` | A negative delay or count; a length prefix other than 1, 2 or 4 |
