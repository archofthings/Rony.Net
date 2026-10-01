# Stateful Scenarios

Real servers remember things. `LIST` only works after `LOGIN`, a job reports `RUNNING` until it is `DONE`, and a
server in maintenance answers everything with an error. [Sequences](Response-Sequences) change the response to one
request; **scenario states** change which rules apply to every request.

## States and transitions
Every server starts in the state `"initial"` (`RequestHandler.InitialState`).
- `.GoTo("state")` after a response moves the scenario to that state once the response is used.
- `InState("state")` before `Send(...)` makes a rule apply only in that state.
- Rules without `InState` apply in every state.

```csharp
server.Mock.Send("LOGIN bob").Receive("OK").GoTo("loggedIn");
server.Mock.InState("loggedIn").Send("LIST").Receive("a,b,c");
server.Mock.InState("loggedIn").Send("LOGOUT").Receive("BYE").GoTo(RequestHandler.InitialState);
server.Mock.Send("LIST").Receive("ERR not logged in");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);
Assert.Equal("ERR not logged in", await client.SendAndReceiveAsync("LIST"));
Assert.Equal("OK", await client.SendAndReceiveAsync("LOGIN bob"));
Assert.Equal("a,b,c", await client.SendAndReceiveAsync("LIST"));
Assert.Equal("BYE", await client.SendAndReceiveAsync("LOGOUT"));
Assert.Equal("ERR not logged in", await client.SendAndReceiveAsync("LIST"));
```

`InState(...)` works with every way of [matching a request](Request-Matching): `Send(text)`, `Send(bytes)`,
`Send(Regex)`, `SendMatching(...)`, `SendMatchingBytes(...)` and `Send("")` for any request in that state.

## Which rule wins
The usual [precedence](Request-Matching#which-response-wins) applies: exact requests, then patterns and predicates,
then "any request". At each of these levels, a rule for the current state wins over a rule without a state.
So an exact `Send("PING")` without a state still wins over `InState("busy").Send(new Regex(".*"))`.

## Sequences and states
Each response in a [sequence](Response-Sequences) can move the scenario, so a sequence can end in a new state:

```csharp
server.Mock.Send("START").Receive("STARTED").GoTo("running");
server.Mock.InState("running").Send("STATUS")
    .Receive("RUNNING 10%")
    .Then("RUNNING 60%")
    .Then("DONE").GoTo("finished");
server.Mock.InState("finished").Send("STATUS").Receive("DONE");
server.Mock.Send("STATUS").Receive("IDLE");
```

The state changes when the server picks the response, before any `.After(delay)`, so the next request already sees
the new state.

## Reading and setting the state
```csharp
Assert.Equal("initial", server.Mock.State);
server.Mock.State = "maintenance";   // start a test in a given state
server.Should().BeInState("maintenance");
```

`server.Mock.Reset()` removes the rules and moves the scenario back to `"initial"`.

## One session per connection
By default there is one state for the whole server, so a `GoTo(...)` on one connection affects every client.
For protocols where every connection is its own session, keep a state per connection:

```csharp
server.Mock.StateScope = StateScope.Connection;
server.Mock.Send("LOGIN bob").Receive("OK").GoTo("loggedIn");
server.Mock.InState("loggedIn").Send("LIST").Receive("a,b,c");
server.Mock.Send("LIST").Receive("ERR not logged in");
server.Start();

using var bob = await TcpTestClient.ConnectAsync(server.Port);
using var stranger = await TcpTestClient.ConnectAsync(server.Port);

await bob.SendAndReceiveAsync("LOGIN bob");
Assert.Equal("a,b,c", await bob.SendAndReceiveAsync("LIST"));
Assert.Equal("ERR not logged in", await stranger.SendAndReceiveAsync("LIST"));

Assert.Equal("loggedIn", server.Connections[0].State);
Assert.Equal("initial", server.Connections[1].State);
```

Every new connection starts in `"initial"`. For UDP, every client address has its own state. Requests passed to
`server.Mock.Match(...)` directly use the server-wide `server.Mock.State`.

A [greeting](Connections-and-Push#greetings-talk-first) can start the session:

```csharp
server.Mock.StateScope = StateScope.Connection;
server.Mock.OnConnect().Receive("+OK POP3 ready").GoTo("authorization");
server.Mock.InState("authorization").Send("USER bob").Receive("+OK");
```

## Debugging
The [log](Logging-and-Diagnostics) shows the state each request was matched in and every state change:

```
[Rony 10:15:02.104] #1 received "LOGIN bob" (matched "LOGIN bob")
[Rony 10:15:02.105] #1 state "initial" -> "loggedIn"
[Rony 10:15:02.118] #1 received "LIST" (matched "LIST" in state "loggedIn")
[Rony 10:15:02.131] #1 received "HELP" (unmatched, state "loggedIn")
```

Runnable code: [`ScenarioSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ScenarioSamples.cs)
