# Configuring Responses

Responses are configured on `server.Mock` in two parts: **which request** (`Send...`), then **what to do** (`Receive...`,
`Disconnect()` or `NoReply()`).

```csharp
server.Mock.Send("version").Receive("1.0.0");
```

You can configure responses before or after `Start()`, and from any thread.

## Text and bytes
```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("version").Receive("1.0.0");
server.Mock.Send(new byte[] { 0x01, 0x02 }).Receive(new byte[] { 0x03, 0x04 });
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
Assert.Equal("1.0.0", await client.SendAndReceiveAsync("version"));

await client.SendAsync(new byte[] { 0x01, 0x02 });
Assert.Equal(new byte[] { 0x03, 0x04 }, await client.ReceiveBytesAsync());
```

Text is sent as UTF-8. Requests are compared byte for byte, so binary data that isn't valid text works too.

## Computing the response from the request
Pass a function to build the response from the request, as text or as bytes:

```csharp
server.Mock.Send("hello").Receive(text => text.ToUpper());
server.Mock.Send(new byte[] { 1, 2, 3 }).Receive(bytes => bytes.Reverse().ToArray());
```

The function runs on every request, so it can also keep state:
```csharp
var counter = 0;
server.Mock.Send("next").Receive(_ => $"#{++counter}");   // "#1", "#2", ...
```

If the function throws, the response is empty. Over TCP, nothing is sent.

> **Note:** an identity lambda such as `x => x` is ambiguous, because it fits both `Func<string, string>` and
> `Func<byte[], byte[]>`. Give the parameter a type: `Receive((byte[] request) => request)`.

## A default response for any request
`Send("")`, an empty request, matches anything that has no more specific response:

```csharp
server.Mock.Send("known").Receive("specific answer");
server.Mock.Send("").Receive("ERROR unknown command");
```

Combined with a function, it makes an echo server:
```csharp
server.Mock.Send("").Receive((byte[] request) => request);
```

## When nothing matches
If a request has no matching response and there is no `Send("")` default:
- **TCP:** the connection is closed without sending anything.
- **UDP:** an empty datagram is sent back.

The request is still recorded, with `Matched == false`, so you can detect it with
[`server.Should().HaveNoUnmatchedRequests()`](Verifying-Requests#strict-mode).

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("known").Receive("yes");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
await client.SendAsync("unknown");

Assert.Equal("", await client.ReadToEndAsync());
```

## Rules
- The same exact request can only be configured once; a second `Send("ping").Receive(...)` throws `ArgumentException`.
  To return different responses over time, use a [sequence](Response-Sequences).
- `Receive(...)` without a `Send(...)` before it throws `InvalidOperationException`.
- An empty response (`Receive("")`) sends nothing over TCP and an empty datagram over UDP.
- `server.Mock.Reset()` removes every response and recorded request.

```csharp
server.Mock.Reset();

Assert.Empty(server.Mock.Configs);
Assert.Empty(server.Mock.ReceivedRequests);
```

## Everything you can chain
```csharp
server.Mock.Send(...)                 // or SendMatching(...), SendMatchingBytes(...), Send(Regex)
    .Receive(...)                     // or Disconnect(), NoReply()
    .After(TimeSpan)                  // delay that response
    .AndDisconnect()                  // close the connection after that response
    .Then(...)                        // next response: text, bytes or a function
    .ThenDisconnect()                 // next time: close without replying
    .ThenNoReply();                   // next time: stay silent
```

More on each part:
- [Request Matching](Request-Matching): regex, predicates and which response wins.
- [Response Sequences](Response-Sequences): `Then(...)`.
- [Simulating Failures](Simulating-Failures): `After`, `Disconnect`, `NoReply`, `AndDisconnect`.

## Checking a configuration without a network
`server.Mock.Match(request)` runs the same lookup the server does and returns the response bytes. It also records the
request and moves sequences forward, just like a real request.

Runnable code: [`ResponseSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ResponseSamples.cs)
