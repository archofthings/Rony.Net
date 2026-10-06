# Rony.Net

**Rony.Net** is a mock server for testing .NET code that talks over the network.
Start a real TCP, TCP + SSL/TLS, UDP or Unix socket server inside your test, tell it how to answer, point your client at it,
and then check what your client sent.

```csharp
using var server = new MockServer(new TcpServer(0));   // 0 = any free port
server.Mock.Send("PING").Receive("PONG");
server.Start();

// ... run the code under test against 127.0.0.1:server.Port ...

server.Should().HaveReceived("PING", Times.Once());
```

## Why use it
- **Real sockets.** Your client code runs unchanged: no interfaces to extract, no fake streams.
- **Any protocol.** Text or binary, one request per connection or many, delimited or length-prefixed messages.
- **Realistic servers.** Greetings, pushed messages, and stateful scenarios such as "log in before listing".
- **Failure testing.** Slow responses, dropped connections, silence, and responses that change over time.
- **Assertions on the client.** Check which requests were sent, how often and in which order, how it used its
  connections, or wait until a request arrives. Fluent assertions included.
- **Easy debugging.** A log of everything the server saw and did, written to your test output.
- **Test-friendly.** Free ports for parallel tests, thread-safe configuration, clear failure messages.

## Install
```console
dotnet add package Rony.Net
```
Works with .NET Core 3.x, .NET 5 and every later version (the package targets `netstandard2.1` and `net8.0`).
It works with any test framework: xUnit, NUnit, MSTest or none at all. Optional packages
(`Rony.Net.Xunit`, `Rony.Net.Xunit.v3`, `Rony.Net.NUnit`, `Rony.Net.MSTest`) remove the setup code; see [Test Framework Integration](Test-Framework-Integration).
To run a mock server without writing code, install the `rony` tool (`Rony.Net.Cli`) or use its Docker image `ghcr.io/archofthings/rony`: see [Standalone Server](Standalone-Server).
To start the tool in a Docker container from a test, use `Rony.Net.Testcontainers`: see [Testcontainers](Standalone-Server#from-a-net-test-with-testcontainers).

## Documentation

| Start here | |
|---|---|
| [Getting Started](Getting-Started) | Install, first test, the namespaces you need |
| [Servers](Servers) | TCP, UDP and Unix socket servers, IPv6, addresses and properties |
| [SSL and TLS](SSL-and-TLS) | Secure servers, certificates for tests |
| [Ports and Lifecycle](Ports-and-Lifecycle) | Free ports, start/stop/restart, parallel tests |
| [Connections and Framing](Connections-and-Framing) | Persistent connections, delimiters, length prefixes, custom framing |
| [Connections and Push](Connections-and-Push) | Greetings, pushed messages, inspecting and checking connections |

| Configure responses | |
|---|---|
| [Configuring Responses](Configuring-Responses) | Text, bytes, computed responses, a default response |
| [Request Matching](Request-Matching) | Exact, regex and predicate matching, and precedence |
| [Response Sequences](Response-Sequences) | A different response each time |
| [Simulating Failures](Simulating-Failures) | Delays, disconnects, silence, flaky servers |
| [Stateful Scenarios](Stateful-Scenarios) | Rules that depend on what happened before (`InState`, `GoTo`) |
| [Record and Replay](Record-and-Replay) | Record a real server with `RecordingProxy`, replay it with `server.Replay(...)` |
| [Configuration Files](Configuration-Files) | Describe the server and its rules in a JSON file, load it with `MockServer.FromFile(...)` |
| [Standalone Server](Standalone-Server) | The `rony` command-line tool and its Docker image (`ghcr.io/archofthings/rony`): run, record and replay without code, with a journal and a control endpoint to check what the mock received. Scenarios for development, teams without .NET, CI and Docker Compose; the `Rony.Net.Testcontainers` module |

| Check your client | |
|---|---|
| [Verifying Requests](Verifying-Requests) | `server.Should()` assertions, `Times`, order, strict mode, inspecting requests |
| [Waiting for Requests](Waiting-for-Requests) | `WaitForRequestAsync` instead of `Thread.Sleep` |
| [Logging and Diagnostics](Logging-and-Diagnostics) | See what the server received and did, and why |
| [Test Framework Integration](Test-Framework-Integration) | Base classes for xUnit, NUnit and MSTest |

| More | |
|---|---|
| [Recipes](Recipes) | Testing a real client class, retries, timeouts, sharing a server between tests |
| [Custom Listeners](Custom-Listeners) | Plug in your own transport |
| [API Reference](API-Reference) | Every public type and member |
| [Troubleshooting](Troubleshooting) | Common problems and answers |
| [Known Issues](Known-Issues) | What the library and the `rony` tool do not do, or do in a surprising way |
| [Upgrading to 1.0](Upgrading-to-1.0) | Changes from 0.x |

## Runnable examples
Every example in this wiki is a passing test in the
[samples project](https://github.com/archofthings/Rony.Net/tree/main/samples/Rony.Samples).
Clone the repository and run `dotnet test samples/Rony.Samples` to try them.
