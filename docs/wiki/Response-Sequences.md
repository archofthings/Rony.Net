# Response Sequences

Use `Then(...)` to return a different response each time the same request arrives.
When the sequence runs out, **the last response repeats**.

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("status").Receive("starting").Then("starting").Then("ready");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
Assert.Equal("starting", await client.SendAndReceiveAsync("status"));
Assert.Equal("starting", await client.SendAndReceiveAsync("status"));
Assert.Equal("ready", await client.SendAndReceiveAsync("status"));
Assert.Equal("ready", await client.SendAndReceiveAsync("status"));   // the last one repeats
```

This is useful for testing:
- **Polling:** "busy, busy, ready".
- **Retries:** "fail, fail, succeed"; see [Simulating Failures](Simulating-Failures#a-flaky-server).
- **Paging:** "page 1, page 2, end".
- **State changes:** "logged out" after a logout command.

## What a step can be
Every step in a sequence can be any kind of response:

```csharp
server.Mock.Send("data")
    .Receive("text")                          // text
    .Then(new byte[] { 0x01 })                // bytes
    .Then(text => text.ToUpper())             // computed from the request
    .Then(bytes => bytes.Reverse().ToArray())
    .ThenNoReply()                            // silence
    .ThenDisconnect();                        // close the connection
```

`After(delay)` and `AndDisconnect()` apply to the step just before them:

```csharp
server.Mock.Send("data")
    .Receive("late").After(TimeSpan.FromMilliseconds(300))
    .Then("quick");
```

A sequence can also start with `Disconnect()` or `NoReply()`:
```csharp
server.Mock.Send("pay").Disconnect().ThenDisconnect().Then("PAID");
```

## Sequences or functions?
A function in `Receive(...)` runs on every request, so it can count calls by itself:
```csharp
var counter = 0;
server.Mock.Send("next").Receive(_ => $"#{++counter}");
```
Use `Then(...)` for a short fixed script, and a function for an open-ended pattern.

## Notes
- A sequence belongs to its configuration, not to a connection: every client moves through the same sequence.
- `Config.CallCount` tells you how many steps have been used.
- `server.Mock.Reset()` removes the configuration, sequence included.

Runnable code: [`SequenceAndFailureSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/SequenceAndFailureSamples.cs)
