# API Reference

Every public type in Rony.Net 1.0. The package includes XML documentation, so IntelliSense shows the same descriptions in your editor.

## `Rony.Net.MockServer`
The mock server. Wraps a listener and answers requests with the responses configured on `Mock`.

| Member | Description |
|---|---|
| `MockServer(IListener listener)` | Creates a server on the given listener |
| `void Start()` | Starts listening. Repeated calls do nothing. |
| `void Stop()` | Stops listening, closes connections and cancels delayed responses. Repeated calls do nothing. |
| `void Dispose()` | Stops the server and releases the listener |
| `bool Active` | Whether the server is started |
| `IPAddress Address` | The listening address |
| `int Port` | The listening port (the assigned one when created with port `0`) |
| `RequestHandler Mock` | Configuration, recording and verification |
| `IReadOnlyList<ReceivedRequest> ReceivedRequests` | Shortcut for `Mock.ReceivedRequests` |

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

**Choosing the response** (each returns a `ResponseBuilder`)

| Member | Effect |
|---|---|
| `Receive(string response)` | Responds with this text |
| `Receive(byte[] response)` | Responds with these bytes |
| `Receive(Func<string, string> func)` | Responds with `func(request text)` |
| `Receive(Func<byte[], byte[]> func)` | Responds with `func(request bytes)` |
| `Disconnect()` | Closes the TCP connection without replying |
| `NoReply()` | Never replies |

**Matching and state**

| Member | Description |
|---|---|
| `byte[] Match(string or byte[] request)` | Runs the server's lookup and returns the response (also records the request) |
| `IReadOnlyDictionary<byte[], Config> Configs` | Exact-request configurations, keyed by request bytes |
| `void Reset()` | Removes every configuration and recorded request |

**Verification**

| Member | Description |
|---|---|
| `IReadOnlyList<ReceivedRequest> ReceivedRequests` | Every request, oldest first |
| `IReadOnlyList<ReceivedRequest> UnmatchedRequests` | Requests with no configured response |
| `void Verify(string or byte[] request)` | At least once |
| `void Verify(string or byte[] request, Times times)` | The given number of times |
| `void Verify(Func<ReceivedRequest, bool> predicate[, Times times])` | Requests satisfying the predicate |
| `void VerifyAllRequestsMatched()` | Every request had a configured response |
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
Returned by `Receive(...)`, `Disconnect()` and `NoReply()`.

| Member | Effect |
|---|---|
| `Then(string / byte[] / Func<string, string> / Func<byte[], byte[]>)` | Adds the next response in the sequence |
| `ThenDisconnect()` | Next time: close without replying |
| `ThenNoReply()` | Next time: no reply |
| `After(TimeSpan delay)` | Delays the previous response |
| `AndDisconnect()` | Closes the connection after the previous response |

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
| `protected abstract Task<Stream> OpenStreamAsync(TcpClient client)` | Prepares the stream for a new connection |
| `protected virtual bool HasPendingData(Stream stream)` | Whether more data can be read right away |

## Framing (`Rony.Listeners.MessageFraming`, `Rony.Interfaces.IMessageFraming`)

| Member | Description |
|---|---|
| `MessageFraming.None` | One burst is one message (default) |
| `MessageFraming.Delimiter(string or byte[] delimiter)` | Messages end with the delimiter |
| `MessageFraming.LengthPrefix(int prefixLength = 4, bool bigEndian = true)` | Messages start with a 1, 2 or 4-byte length |
| `IMessageFraming.Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)` | Extracts complete messages |
| `IMessageFraming.Encode(byte[] response)` | Frames a response |

## `Rony.Net.Times`
`Never()`, `Once()`, `AtLeastOnce()`, `Exactly(n)`, `AtLeast(n)`, `AtMost(n)`, `Between(min, max)`, plus `Matches(count)`, `Min` and `Max`.

## `Rony.Models.ReceivedRequest`
`Body` (bytes), `BodyString` (UTF-8 text), `RemoteEndPoint`, `Timestamp`, `Matched`.

## `Rony.Models.Config`
One exact-request configuration: `CallCount`, and `GetResponse(string or byte[])`, which returns the next response and moves the sequence forward.

## `Rony.Interfaces.IListener`
The transport contract; see [Custom Listeners](Custom-Listeners).

## `Rony.Models.Message`
A request as delivered by a listener: `Body`, `BodyString`, `Sender`, `RemoteEndPoint`.

## `Rony.Convertor`
UTF-8 extension methods: `string.GetBytes()` and `byte[].GetString()`.

## Exceptions

| Exception | Thrown by |
|---|---|
| `MockVerificationException` | `Verify(...)`, `VerifyAllRequestsMatched()` |
| `TimeoutException` | `WaitForRequestAsync(...)`, `WaitForRequestsAsync(...)` |
| `ArgumentException` | Configuring the same exact request twice; an empty delimiter |
| `InvalidOperationException` | `Receive(...)` without `Send(...)`; a response too long for its length prefix |
| `ArgumentOutOfRangeException` | A negative delay or count; a length prefix other than 1, 2 or 4 |
