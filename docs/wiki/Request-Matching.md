# Request Matching

## Exact requests
`Send("text")` and `Send(bytes)` match a request with exactly those bytes:

```csharp
server.Mock.Send("LIST").Receive("a,b,c");
server.Mock.Send(new byte[] { 0x01, 0x02 }).Receive(new byte[] { 0x06 });
```

With [framing](Connections-and-Framing), the delimiter or length prefix is removed before matching.

## Regular expressions
For requests with changing parts, such as IDs, timestamps or credentials:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send(new Regex(@"^LOGIN \w+ \w+$")).Receive("OK");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
Assert.Equal("OK", await client.SendAndReceiveAsync("LOGIN alice secret"));
```

The regular expression is applied to the request decoded as UTF-8 text.

## Predicates
For anything a regex can't express, pass a function that decides whether a request matches. `SendMatching` gets the
request as text, and `SendMatchingBytes` gets the raw bytes:

```csharp
server.Mock.SendMatching(text => text.StartsWith("GET ")).Receive("200 OK");
server.Mock.SendMatchingBytes(bytes => bytes.Length > 0 && bytes[0] == 0xFF).Receive(new byte[] { 0x00 });
```

A predicate that throws, for example `bytes[10]` on a shorter request, simply doesn't match.

## Echoing part of the request
Combine a pattern with a response function:

```csharp
// The client sends "ORDER <id>" and expects "ACK <id>" back.
server.Mock.Send(new Regex(@"^ORDER \d+$")).Receive(request => "ACK " + request.Split(' ')[1]);

Assert.Equal("ACK 1001", await client.SendAndReceiveAsync("ORDER 1001"));
Assert.Equal("ACK 1002", await client.SendAndReceiveAsync("ORDER 1002"));
```

## Which response wins
When several configurations could match a request, the server picks the first that applies in this order:

1. **Exact request:** `Send("ABC")` or `Send(bytes)`.
2. **Patterns and predicates:** `Send(Regex)`, `SendMatching`, `SendMatchingBytes`, in the order you added them.
3. **Any request:** `Send("")`.

```csharp
server.Mock.Send("").Receive("any");                               // 3. fallback
server.Mock.SendMatching(text => text.StartsWith("A")).Receive("A*"); // 2. patterns, in the order added
server.Mock.Send("ABC").Receive("exact");                          // 1. exact request

Assert.Equal("exact", server.Mock.Match("ABC").GetString());
Assert.Equal("A*", server.Mock.Match("AXY").GetString());
Assert.Equal("any", server.Mock.Match("XYZ").GetString());
```

Because patterns are tried in order, add the more specific ones first:
```csharp
server.Mock.Send(new Regex("^GET /admin")).Receive("403 Forbidden");
server.Mock.Send(new Regex("^GET ")).Receive("200 OK");
```

## Inspecting the configuration
`server.Mock.Configs` lists the exact-request configurations, keyed by request bytes. Patterns and predicates aren't
included. Each `Config` has a `CallCount`:

```csharp
Assert.Equal(2, server.Mock.Configs["LIST".GetBytes()].CallCount);
```

To check how often a request arrived, [`Verify`](Verifying-Requests) is usually clearer.

Runnable code: [`ResponseSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ResponseSamples.cs)
