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

## JSON requests
`SendJson` matches requests that are valid JSON and satisfy a function on the parsed document. `JsonValue` is read-only and
indexing never throws, so a missing field is just `Undefined`:

```csharp
server.Mock.SendJson(j => j["type"].AsString() == "login").Receive("{\"ok\":true}");
server.Mock.SendJson(j => j["items"].Count > 2 && j["user"]["roles"][0].AsString() == "admin").Receive("big admin order");

Assert.Equal("{\"ok\":true}", server.Mock.Match("{\"type\":\"login\",\"user\":\"bob\"}").GetString());
Assert.Empty(server.Mock.Match("not json"));   // not JSON: no match, and no error is logged
```

A request that isn't valid JSON doesn't match and the function isn't called; a function that throws doesn't match either.
A JSON rule is a predicate rule, so it has the same place as `SendMatching` in [which response wins](#which-response-wins).
There is no `ReceiveJson`: to build a response from the request, parse it yourself:

```csharp
server.Mock.SendJson(j => j["id"].Exists)
    .Receive(request => "{\"echo\":" + JsonValue.Parse(request)["id"] + "}");

Assert.Equal("{\"echo\":42}", server.Mock.Match("{\"id\":42}").GetString());
```

`JsonValue.Parse(text)` throws `FormatException` for invalid JSON, and `TryParse` returns false.

## Echoing part of the request
Combine a pattern with a response function:

```csharp
// The client sends "ORDER <id>" and expects "ACK <id>" back.
server.Mock.Send(new Regex(@"^ORDER \d+$")).Receive(request => "ACK " + request.Split(' ')[1]);

Assert.Equal("ACK 1001", await client.SendAndReceiveAsync("ORDER 1001"));
Assert.Equal("ACK 1002", await client.SendAndReceiveAsync("ORDER 1002"));
```

### Capture groups
`ReceiveMatch` (and `ThenMatch` in a [sequence](Response-Sequences)) passes the regular expression `Match`, so you can use
groups. They only exist after `Send(Regex)`; on any other rule they throw `InvalidOperationException`. `Receive(...)` was
deliberately not extended with a `Func<Match, string>` overload, because `Receive(x => "pong")` would become ambiguous.

```csharp
server.Mock.Send(new Regex(@"^HELLO (\w+)$")).ReceiveMatch(m => $"HI {m.Groups[1].Value}");

Assert.Equal("HI bob", server.Mock.Match("HELLO bob").GetString());
```

## Which response wins
When several configurations could match a request, the server picks the first that applies in this order:

1. **Exact request:** `Send("ABC")` or `Send(bytes)`.
2. **Patterns and predicates:** `Send(Regex)`, `SendMatching`, `SendMatchingBytes`, `SendJson`, in the order you added them.
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

With [scenario states](Stateful-Scenarios), a rule for the current state wins over a rule without a state at each of these levels.

## Unmatched requests
A request that no rule matches is recorded as unmatched. By default the server sends nothing back (UDP: an empty
datagram) and closes the connection:

```csharp
server.Mock.Send("PING").Receive("PONG");
// client sends "PNIG" → the connection is closed
```

`OnUnmatched()` chooses a different reaction, with everything a response can do:

```csharp
server.Mock.OnUnmatched().Receive(text => $"ERR unknown command '{text}'");   // answer, keep the connection
server.Mock.OnUnmatched().NoReply();                                          // ignore, keep the connection
server.Mock.OnUnmatched().Receive("ERR").AndDisconnect();                     // answer, then hang up
```

The difference from a `Send("")` default: requests answered by `OnUnmatched()` still count as **unmatched**, so
[strict mode](Verifying-Requests#strict-mode) and [`FailOnUnmatched`](Verifying-Requests#fail-fast-on-unexpected-requests)
still report them. Use `Send("")` when "anything else" is a normal part of the protocol, and `OnUnmatched()` when it means
the client did something wrong.

```csharp
server.Mock.Send("PING").Receive("PONG");
server.Mock.OnUnmatched().Receive(text => $"ERR unknown command '{text}'");

Assert.Equal("ERR unknown command 'PNIG'", await client.SendAndReceiveAsync("PNIG"));
Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));   // still connected
Assert.Single(server.Mock.UnmatchedRequests);                     // still reported
```

## Inspecting the configuration
`server.Mock.Configs` lists the exact-request configurations, keyed by request bytes. Patterns, predicates and rules
for a [state](Stateful-Scenarios) aren't included. Each `Config` has a `CallCount`:

```csharp
Assert.Equal(2, server.Mock.Configs["LIST".GetBytes()].CallCount);
```

To check how often a request arrived, [`server.Should().HaveReceived(...)`](Verifying-Requests) is usually clearer.

Runnable code: [`PartialMatchingSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/PartialMatchingSamples.cs),
[`ResponseSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ResponseSamples.cs),
[`UnmatchedRequestSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/UnmatchedRequestSamples.cs)
