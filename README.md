# Rony.Net

[![CI](https://github.com/archofthings/Rony.Net/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/archofthings/Rony.Net/actions/workflows/ci.yml)
[![NuGet version](https://img.shields.io/nuget/v/Rony.Net.svg?logo=nuget)](https://www.nuget.org/packages/Rony.Net)
[![NuGet downloads](https://img.shields.io/nuget/dt/Rony.Net.svg?logo=nuget)](https://www.nuget.org/packages/Rony.Net)
[![.NET](https://img.shields.io/badge/.NET-netstandard2.1%20%7C%20net8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Docs](https://img.shields.io/badge/docs-wiki-blue?logo=github)](https://github.com/archofthings/Rony.Net/wiki)
[![License: MIT](https://img.shields.io/github/license/archofthings/Rony.Net.svg)](https://github.com/archofthings/Rony.Net/blob/main/LICENSE)

A mock server for testing .NET code that talks over the network.
Start a real **TCP**, **TCP + SSL/TLS** or **UDP** server inside your test, tell it how to answer, point your client
at it, and then check what your client sent.

```csharp
using var server = new MockServer(new TcpServer(0));   // 0 = any free port
server.Mock.Send("PING").Receive("PONG");
server.Start();

// ... run the code under test against 127.0.0.1:server.Port ...

server.Should().HaveReceived("PING", Times.Once());
```

📖 **Full documentation, with an example for every feature, is in the [wiki](https://github.com/archofthings/Rony.Net/wiki).**

## Features
- **Real sockets.** Your client code runs unchanged: no interfaces to extract, no fake streams. Includes TLS with a built-in test certificate and mutual TLS, IPv6, dual-stack and Unix domain sockets. → [Servers](https://github.com/archofthings/Rony.Net/wiki/Servers), [SSL and TLS](https://github.com/archofthings/Rony.Net/wiki/SSL-and-TLS)
- **Free ports and a clean lifecycle.** Port `0` means tests never fight over ports, even in parallel; start and stop synchronously or with `await using`. → [Ports and Lifecycle](https://github.com/archofthings/Rony.Net/wiki/Ports-and-Lifecycle)
- **Any protocol.** Text or binary; persistent connections; delimited, length-prefixed, fixed-length, STX/ETX or custom messages. → [Connections and Framing](https://github.com/archofthings/Rony.Net/wiki/Connections-and-Framing)
- **Flexible matching.** Exact requests, regular expressions with capture groups, JSON fields, predicates, a default response and a handler for unmatched requests. → [Request Matching](https://github.com/archofthings/Rony.Net/wiki/Request-Matching)
- **Scripted responses.** Fixed, computed from the request, or a different one each time. → [Configuring Responses](https://github.com/archofthings/Rony.Net/wiki/Configuring-Responses), [Response Sequences](https://github.com/archofthings/Rony.Net/wiki/Response-Sequences)
- **Server-initiated messages.** Greetings on connect, pushed messages and broadcasts; connection list and events. → [Connections and Push](https://github.com/archofthings/Rony.Net/wiki/Connections-and-Push)
- **Stateful scenarios.** "`LIST` only works after `LOGIN`", for the whole server or per connection. → [Stateful Scenarios](https://github.com/archofthings/Rony.Net/wiki/Stateful-Scenarios)
- **Failure testing.** Delays, chunked and throttled responses, dropped and reset connections, truncated or corrupted responses, refused connections, failing TLS handshakes, silence and flaky servers. → [Simulating Failures](https://github.com/archofthings/Rony.Net/wiki/Simulating-Failures)
- **Assertions on your client.** Fluent `server.Should()` and `connection.Should()` assertions with `Times`, order, strict or fail-fast mode, connection checks, TLS details (protocol, server name, client certificate), and waiting for requests and connections without sleeps. → [Verifying Requests](https://github.com/archofthings/Rony.Net/wiki/Verifying-Requests), [Waiting for Requests](https://github.com/archofthings/Rony.Net/wiki/Waiting-for-Requests)
- **Record and replay.** Record the conversation with a real server through a proxy, save it as an editable JSON file and replay it as a mock server. → [Record and Replay](https://github.com/archofthings/Rony.Net/wiki/Record-and-Replay)
- **Configuration files.** Describe the server and its rules in a JSON file and load it with one call. → [Configuration Files](https://github.com/archofthings/Rony.Net/wiki/Configuration-Files)
- **Standalone server.** The `rony` command-line tool (and a Dockerfile in the repository to build an image) runs a configuration file, record a real server and replay the recording, with no .NET test code. → [Standalone Server](https://github.com/archofthings/Rony.Net/wiki/Standalone-Server)
- **Easy debugging.** A log of every connection, request, matched rule, response and error. → [Logging and Diagnostics](https://github.com/archofthings/Rony.Net/wiki/Logging-and-Diagnostics)
- **Works everywhere.** .NET Core 3.x and every later .NET, with xUnit v2 or v3, NUnit or MSTest (with optional base classes), on Windows, Linux and macOS. → [Test Framework Integration](https://github.com/archofthings/Rony.Net/wiki/Test-Framework-Integration)

## Install
```console
dotnet add package Rony.Net
```
Or in the Package Manager Console: `Install-Package Rony.Net`.

Optional, for less setup code: `Rony.Net.Xunit` (xUnit v2), `Rony.Net.Xunit.v3` (xUnit v3), `Rony.Net.NUnit` or `Rony.Net.MSTest`
([Test Framework Integration](https://github.com/archofthings/Rony.Net/wiki/Test-Framework-Integration)).
To run a mock server without code: the `Rony.Net.Cli` tool ([Standalone Server](https://github.com/archofthings/Rony.Net/wiki/Standalone-Server)).

## Quick start
```csharp
using Rony;            // GetBytes() / GetString() helpers
using Rony.Listeners;  // TcpServer, TcpServerSsl, UdpServer, MessageFraming
using Rony.Net;        // MockServer, Times

[Fact]
public async Task Client_gets_pong()
{
    using var server = new MockServer(new TcpServer(0));
    server.Mock.Send("PING").Receive("PONG");
    server.Start();

    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, server.Port);
    var stream = client.GetStream();
    await stream.WriteAsync("PING".GetBytes());

    var buffer = new byte[1024];
    var read = await stream.ReadAsync(buffer);

    Assert.Equal("PONG", buffer[..read].GetString());
    server.Should().HaveReceived("PING", Times.Once());
}
```
More in [Getting Started](https://github.com/archofthings/Rony.Net/wiki/Getting-Started).

## Servers
```csharp
new MockServer(new TcpServer(0));                                    // TCP on 127.0.0.1
new MockServer(new TcpServerSsl(0, certificate, SslProtocols.None)); // TCP + SSL/TLS
new MockServer(new UdpServer("127.0.0.1", 0));                       // UDP
new MockServer(new UnixSocketServer());                              // Unix domain socket; the file is listener.Path
new TcpServer(IPAddress.IPv6Any, 0) { DualMode = true };             // IPv4 and IPv6 on one port (UdpServer: new UdpServer(endPoint, dualMode: true))
using var certificate = TestCertificate.CreateSelfSigned();          // in-memory certificate for TLS tests
new TcpServerSsl(0, certificate, SslProtocols.Tls12) { RequireClientCertificate = true };   // mutual TLS
connection.Should().HaveUsedTls(SslProtocols.Tls12).And.HavePresentedClientCertificate();   // TLS details of a connection

await using var server = new MockServer(new TcpServer(0));           // async lifecycle: also StartAsync(), StopAsync()
```
TCP connections stay open, so a client can send many requests over one connection. Each connection is handled
independently, and responses keep their order. `await using` and `StopAsync()` wait for the server's background work,
so no callback or log line runs after the test.
Details: [Servers](https://github.com/archofthings/Rony.Net/wiki/Servers) · [SSL and TLS](https://github.com/archofthings/Rony.Net/wiki/SSL-and-TLS) (test certificate, mutual TLS, TLS assertions and failing the handshake) ·
[Ports and Lifecycle](https://github.com/archofthings/Rony.Net/wiki/Ports-and-Lifecycle)

## Message framing
TCP doesn't keep message boundaries. Tell the server where messages end, and it splits requests and frames responses for you:
```csharp
new TcpServer(0) { Framing = MessageFraming.Delimiter("\r\n") };   // line-based protocols
new TcpServer(0) { Framing = MessageFraming.LengthPrefix(2) };     // binary, length-prefixed
new TcpServer(0) { Framing = MessageFraming.LengthPrefix(4, bigEndian: false) };                       // little-endian
new TcpServer(0) { Framing = MessageFraming.LengthPrefix(2, bigEndian: true, includesPrefix: true) };  // length counts itself
new TcpServer(0) { Framing = MessageFraming.FixedLength(8) };      // fixed-size records
new TcpServer(0) { Framing = MessageFraming.StxEtx };              // 0x02 ... 0x03; StartEnd(start, end) for other bytes
new TcpServer(0) { KeepAlive = false };                            // close after every response
```
Details, and custom framing: [Connections and Framing](https://github.com/archofthings/Rony.Net/wiki/Connections-and-Framing)

## Configuring responses
```csharp
server.Mock.Send("version").Receive("1.0.0");                                  // text
server.Mock.Send(new byte[] { 0x01, 0x02 }).Receive(new byte[] { 0x03 });      // bytes
server.Mock.Send("hello").Receive(text => text.ToUpper());                     // computed
server.Mock.Send("").Receive("ERROR unknown command");                         // any other request
```
Details: [Configuring Responses](https://github.com/archofthings/Rony.Net/wiki/Configuring-Responses)

### Matching
```csharp
server.Mock.Send(new Regex(@"^LOGIN \w+$")).Receive("WELCOME");
server.Mock.SendMatching(text => text.StartsWith("GET ")).Receive("200 OK");
server.Mock.SendMatchingBytes(bytes => bytes[0] == 0x02).Receive(new byte[] { 0x06 });
server.Mock.Send(new Regex(@"^HELLO (\w+)$")).ReceiveMatch(m => $"HI {m.Groups[1].Value}");
server.Mock.SendJson(j => j["type"].AsString() == "login").Receive("{\"ok\":true}");
```
An exact request wins over patterns and predicates, which win over the `Send("")` default.
Details: [Request Matching](https://github.com/archofthings/Rony.Net/wiki/Request-Matching)

### Sequences
```csharp
server.Mock.Send("status").Receive("busy").Then("busy").Then("ready");   // busy, busy, ready, ready, ...
```
Details: [Response Sequences](https://github.com/archofthings/Rony.Net/wiki/Response-Sequences)

### Failures
```csharp
server.Mock.Send("report").Receive("done").After(TimeSpan.FromSeconds(2));   // slow
server.Mock.Send("pay").Disconnect().Then("PAID");                           // drop once, then succeed
server.Mock.Send("ping").NoReply();                                          // never answer
server.Mock.Send("QUIT").Receive("BYE").AndDisconnect();                     // reply, then hang up
server.Mock.Send("X").ResetConnection();                                     // TCP reset instead of a clean close
server.Mock.Send("X").Receive("HELLO WORLD").Truncated(5).AndDisconnect();   // only the first 5 bytes
server.Mock.Send("X").Receive("HELLO").Corrupted(bytes => { bytes[0] ^= 0xFF; return bytes; });
server.Mock.Send("GET").Receive(body).InChunks(16, TimeSpan.FromMilliseconds(50));   // piece by piece
server.Mock.Send("GET").Receive(body).Throttled(bytesPerSecond: 1024);               // slow link
server.RefuseConnections();                                                  // "connection refused" until AcceptConnections()
new TcpServerSsl(0, certificate, SslProtocols.Tls12) { FailHandshake = true };   // every TLS handshake fails
```
Details: [Simulating Failures](https://github.com/archofthings/Rony.Net/wiki/Simulating-Failures)

### Unmatched requests
```csharp
server.Mock.OnUnmatched().Receive(text => $"ERR unknown command '{text}'");   // answer, keep the connection
server.Mock.FailOnUnmatched = true;                                           // fail Verify/WaitFor right away
```
Details: [Request Matching](https://github.com/archofthings/Rony.Net/wiki/Request-Matching#unmatched-requests)

### Stateful scenarios
```csharp
server.Mock.Send("LOGIN bob").Receive("OK").GoTo("loggedIn");
server.Mock.InState("loggedIn").Send("LIST").Receive("a,b,c");
server.Mock.Send("LIST").Receive("ERR not logged in");
server.Mock.StateScope = StateScope.Connection;                  // optional: a session per connection
```
Details: [Stateful Scenarios](https://github.com/archofthings/Rony.Net/wiki/Stateful-Scenarios)

## Record and replay
```csharp
using var proxy = new RecordingProxy("real.host", 5000) { Framing = MessageFraming.Delimiter("\n") };
proxy.Start();                                            // point your client at proxy.Port
await proxy.WaitForConnectionsClosedAsync();
proxy.Recording.Save("login.rony.json");                  // editable JSON

server.Replay(Recording.Load("login.rony.json"));         // later, in tests: rules from the recording
```
Details: [Record and Replay](https://github.com/archofthings/Rony.Net/wiki/Record-and-Replay)

## Configuration files
```csharp
// mock.json: { "version": 1, "rules": [ { "request": "PING", "reply": "PONG" } ] }
using var server = MockServer.FromFile("mock.json");   // listener and rules from the file
server.Start();
```
Details: [Configuration Files](https://github.com/archofthings/Rony.Net/wiki/Configuration-Files)

## Standalone server
```console
dotnet tool install --global Rony.Net.Cli
rony run mock.json                                   # serve a configuration file (Ctrl+C stops it)
rony record --target api.test:5000 --out login.json  # record a real server through a proxy
rony replay login.json                               # serve the recording
docker run --rm -p 4000:4000 -v "$PWD:/config" rony   # the same as a Docker image (build it from the Dockerfile)
```
Details: [Standalone Server](https://github.com/archofthings/Rony.Net/wiki/Standalone-Server)

## Connections and pushed messages
```csharp
server.Mock.OnConnect().Receive("220 mail.test ready\r\n");     // greet every client first
await server.Connections[0].SendAsync("NOTIFY price-changed");   // push to one client
await server.BroadcastAsync("SHUTDOWN in 5 minutes");            // or to all of them

server.Should().HaveAcceptedConnections(Times.Once());           // the client reused its connection
await server.Connections[0].WaitForCloseAsync();                 // and closed it
await server.WaitForAllConnectionsClosedAsync();                 // or wait for all of them
server.Should().HaveNoOpenConnections();
```
Details: [Connections and Push](https://github.com/archofthings/Rony.Net/wiki/Connections-and-Push)

## Checking what your client sent
```csharp
server.Should().HaveReceived("LIST", Times.Exactly(2))
    .And.HaveReceived(r => r.BodyString.StartsWith("LOGIN"), Times.Once())
    .And.NotHaveReceived("DELETE")
    .And.HaveReceivedInOrder("LOGIN", "LIST", "QUIT")   // order (others may be in between)
    .And.HaveNoUnmatchedRequests();                     // strict mode

await server.Mock.WaitForRequestAsync("HEARTBEAT");     // instead of Thread.Sleep
var requests = server.ReceivedRequests;                 // body, sender, time, matched

connection.Should().HaveReceived("LOGIN bob", Times.Once())   // only what one connection sent
    .And.HaveReceivedInOrder("LOGIN bob", "LIST")
    .And.BeClosed();
```
A failed check lists every request the server received. The same checks are also available as
`server.Mock.Verify(...)` methods.
Details: [Verifying Requests](https://github.com/archofthings/Rony.Net/wiki/Verifying-Requests) · [Waiting for Requests](https://github.com/archofthings/Rony.Net/wiki/Waiting-for-Requests)

## Logging
```csharp
server.Log = output.WriteLine;   // xUnit's ITestOutputHelper, Console.WriteLine, ...
```
```
[Rony 10:15:02.097] #1 connected from 127.0.0.1:50124
[Rony 10:15:02.102] #1 received "PING" (matched "PING")
[Rony 10:15:02.103] #1 sent "PONG"
```
Errors that are otherwise silent, such as an exception in a `Receive(...)` function or a failed TLS handshake, are logged too.
Details: [Logging and Diagnostics](https://github.com/archofthings/Rony.Net/wiki/Logging-and-Diagnostics)

## Test framework packages
```csharp
public class PingTests : MockServerTest          // Rony.Net.Xunit or Rony.Net.Xunit.v3; also Rony.Net.NUnit and Rony.Net.MSTest
{
    public PingTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public async Task Client_gets_pong()
    {
        Server.Mock.Send("PING").Receive("PONG");   // a started server per test, logging to the test output
        // ...
    }
}
```
Details: [Test Framework Integration](https://github.com/archofthings/Rony.Net/wiki/Test-Framework-Integration)

## More
- [Recipes](https://github.com/archofthings/Rony.Net/wiki/Recipes): testing a real client class with retries and timeouts; xUnit, NUnit and MSTest setup.
- [Custom Listeners](https://github.com/archofthings/Rony.Net/wiki/Custom-Listeners): mock over your own transport, or with no network at all; optional interfaces add connections and failure simulation.
- [API Reference](https://github.com/archofthings/Rony.Net/wiki/API-Reference) · [Troubleshooting](https://github.com/archofthings/Rony.Net/wiki/Troubleshooting)
- [Runnable samples](https://github.com/archofthings/Rony.Net/tree/main/samples/Rony.Samples): every wiki example as a passing test.

## Upgrading from 0.x
1.0 keeps TCP connections open after a response. If your client reads until the server closes the connection, set
`KeepAlive = false`. See [Upgrading to 1.0](https://github.com/archofthings/Rony.Net/wiki/Upgrading-to-1.0) and the
[changelog](https://github.com/archofthings/Rony.Net/blob/main/CHANGELOG.md).

## Why Rony.Net
While working on [Cimon.Net](https://github.com/MojtabaKiani/Cimon.Net), I couldn't find a library that mocks sockets.
Faking sockets inside the project didn't really solve the problem, so I wrote this library and used it in Cimon.Net.

## Build and test
You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or later.
```console
dotnet build
dotnet test
```
The tests need no setup: SSL/TLS tests create their certificate at runtime. CI runs everything, including the samples, on Linux and Windows.
The wiki source lives in [`docs/wiki`](https://github.com/archofthings/Rony.Net/tree/main/docs/wiki) and is published automatically.

## License
[MIT](https://github.com/archofthings/Rony.Net/blob/main/LICENSE)
