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

server.Mock.Verify("PING", Times.Once());
```

📖 **Full documentation, with an example for every feature, is in the [wiki](https://github.com/archofthings/Rony.Net/wiki).**

## Features
- **Real sockets.** Your client code runs unchanged: no interfaces to extract, no fake streams. → [Servers](https://github.com/archofthings/Rony.Net/wiki/Servers), [SSL and TLS](https://github.com/archofthings/Rony.Net/wiki/SSL-and-TLS)
- **Free ports.** Port `0` means tests never fight over ports, even in parallel. → [Ports and Lifecycle](https://github.com/archofthings/Rony.Net/wiki/Ports-and-Lifecycle)
- **Any protocol.** Text or binary; persistent connections; delimited, length-prefixed or custom messages. → [Connections and Framing](https://github.com/archofthings/Rony.Net/wiki/Connections-and-Framing)
- **Flexible matching.** Exact requests, regular expressions, predicates and a default response. → [Request Matching](https://github.com/archofthings/Rony.Net/wiki/Request-Matching)
- **Scripted responses.** Fixed, computed from the request, or a different one each time. → [Configuring Responses](https://github.com/archofthings/Rony.Net/wiki/Configuring-Responses), [Response Sequences](https://github.com/archofthings/Rony.Net/wiki/Response-Sequences)
- **Failure testing.** Delays, dropped connections, silence and flaky servers. → [Simulating Failures](https://github.com/archofthings/Rony.Net/wiki/Simulating-Failures)
- **Assertions on your client.** `Verify` with `Times`, strict mode, and waiting for a request without sleeps. → [Verifying Requests](https://github.com/archofthings/Rony.Net/wiki/Verifying-Requests), [Waiting for Requests](https://github.com/archofthings/Rony.Net/wiki/Waiting-for-Requests)
- **Works everywhere.** .NET Core 3.x and every later .NET, with xUnit, NUnit or MSTest, on Windows, Linux and macOS.

## Install
```console
dotnet add package Rony.Net
```
Or in the Package Manager Console: `Install-Package Rony.Net`.

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
    server.Mock.Verify("PING", Times.Once());
}
```
More in [Getting Started](https://github.com/archofthings/Rony.Net/wiki/Getting-Started).

## Servers
```csharp
new MockServer(new TcpServer(0));                                    // TCP on 127.0.0.1
new MockServer(new TcpServerSsl(0, certificate, SslProtocols.None)); // TCP + SSL/TLS
new MockServer(new UdpServer("127.0.0.1", 0));                       // UDP
```
TCP connections stay open, so a client can send many requests over one connection. Each connection is handled
independently, and responses keep their order.
Details: [Servers](https://github.com/archofthings/Rony.Net/wiki/Servers) · [SSL and TLS](https://github.com/archofthings/Rony.Net/wiki/SSL-and-TLS) (including creating a test certificate in code) ·
[Ports and Lifecycle](https://github.com/archofthings/Rony.Net/wiki/Ports-and-Lifecycle)

## Message framing
TCP doesn't keep message boundaries. Tell the server where messages end, and it splits requests and frames responses for you:
```csharp
new TcpServer(0) { Framing = MessageFraming.Delimiter("\r\n") };   // line-based protocols
new TcpServer(0) { Framing = MessageFraming.LengthPrefix(2) };     // binary, length-prefixed
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
```
Details: [Simulating Failures](https://github.com/archofthings/Rony.Net/wiki/Simulating-Failures)

## Checking what your client sent
```csharp
server.Mock.Verify("LIST", Times.Exactly(2));
server.Mock.Verify(r => r.BodyString.StartsWith("LOGIN"), Times.Once());
server.Mock.VerifyAllRequestsMatched();                         // strict mode

await server.Mock.WaitForRequestAsync("HEARTBEAT");            // instead of Thread.Sleep
var requests = server.ReceivedRequests;                         // body, sender, time, matched
```
A failed check lists every request the server received.
Details: [Verifying Requests](https://github.com/archofthings/Rony.Net/wiki/Verifying-Requests) · [Waiting for Requests](https://github.com/archofthings/Rony.Net/wiki/Waiting-for-Requests)

## More
- [Recipes](https://github.com/archofthings/Rony.Net/wiki/Recipes): testing a real client class with retries and timeouts; xUnit, NUnit and MSTest setup.
- [Custom Listeners](https://github.com/archofthings/Rony.Net/wiki/Custom-Listeners): mock over your own transport, or with no network at all.
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
