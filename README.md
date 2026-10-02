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
Each line links to the wiki page with the details and examples.
- **Real sockets.** TCP, TCP + SSL/TLS and UDP servers; your client code runs unchanged. → [Servers](https://github.com/archofthings/Rony.Net/wiki/Servers), [SSL and TLS](https://github.com/archofthings/Rony.Net/wiki/SSL-and-TLS)
- **Free ports and a clean lifecycle.** Port `0` picks a free port, so parallel tests never collide; start, stop and restart, synchronously or with `await using`, `StartAsync()` and `StopAsync()`. → [Ports and Lifecycle](https://github.com/archofthings/Rony.Net/wiki/Ports-and-Lifecycle)
- **Any protocol.** Text or binary; persistent connections or close after every response; messages split by delimiter, length prefix (big- or little-endian, optionally counting itself), fixed length, start and end bytes (STX/ETX), or your own framing. → [Connections and Framing](https://github.com/archofthings/Rony.Net/wiki/Connections-and-Framing)
- **Flexible matching.** Exact requests, regular expressions, text or byte predicates, a default response, and a handler for unmatched requests. → [Request Matching](https://github.com/archofthings/Rony.Net/wiki/Request-Matching)
- **Scripted responses.** Fixed, computed from the request, or a different one each time. → [Configuring Responses](https://github.com/archofthings/Rony.Net/wiki/Configuring-Responses), [Response Sequences](https://github.com/archofthings/Rony.Net/wiki/Response-Sequences)
- **Stateful scenarios.** "`LIST` only works after `LOGIN`", for the whole server or per connection. → [Stateful Scenarios](https://github.com/archofthings/Rony.Net/wiki/Stateful-Scenarios)
- **Server-initiated messages.** Greetings on connect, pushed messages and broadcasts; a list of connections and connection events. → [Connections and Push](https://github.com/archofthings/Rony.Net/wiki/Connections-and-Push)
- **Failure testing.** Delays, chunked and throttled responses, silence, dropped and reset connections, truncated or corrupted responses, refused connections, failing TLS handshakes and flaky servers. → [Simulating Failures](https://github.com/archofthings/Rony.Net/wiki/Simulating-Failures), [SSL and TLS](https://github.com/archofthings/Rony.Net/wiki/SSL-and-TLS#failing-the-handshake)
- **Assertions on your client.** Fluent `server.Should()` and `connection.Should()` with `Times`, order, strict or fail-fast mode, connection counts and "no open connections"; the same checks as `Verify(...)` methods. → [Verifying Requests](https://github.com/archofthings/Rony.Net/wiki/Verifying-Requests)
- **No sleeps.** Wait for a request, a connection, a connection to close, or all of them to close. → [Waiting for Requests](https://github.com/archofthings/Rony.Net/wiki/Waiting-for-Requests), [Connections and Push](https://github.com/archofthings/Rony.Net/wiki/Connections-and-Push)
- **Easy debugging.** A log of every connection, request, matched rule, response and error. → [Logging and Diagnostics](https://github.com/archofthings/Rony.Net/wiki/Logging-and-Diagnostics)
- **Test framework packages.** A base class that starts a server per test and logs to the test output, for xUnit v2, xUnit v3, NUnit and MSTest. → [Test Framework Integration](https://github.com/archofthings/Rony.Net/wiki/Test-Framework-Integration)
- **Your own transport.** Implement `IListener`, plus optional interfaces for connections and failure simulation. → [Custom Listeners](https://github.com/archofthings/Rony.Net/wiki/Custom-Listeners)
- **Works everywhere.** .NET Core 3.x and every later .NET, on Windows, Linux and macOS.

## Install
```console
dotnet add package Rony.Net
```
Or in the Package Manager Console: `Install-Package Rony.Net`.

Optional, for less setup code: `Rony.Net.Xunit` (xUnit v2), `Rony.Net.Xunit.v3` (xUnit v3), `Rony.Net.NUnit` or `Rony.Net.MSTest`
([Test Framework Integration](https://github.com/archofthings/Rony.Net/wiki/Test-Framework-Integration)).

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

## More
- [Recipes](https://github.com/archofthings/Rony.Net/wiki/Recipes): testing a real client class with retries and timeouts; xUnit, NUnit and MSTest setup.
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
