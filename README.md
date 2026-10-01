# Rony.Net

[![CI](https://github.com/archofthings/Rony.Net/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/archofthings/Rony.Net/actions/workflows/ci.yml)
[![NuGet version](https://img.shields.io/nuget/v/Rony.Net.svg?logo=nuget)](https://www.nuget.org/packages/Rony.Net)
[![NuGet downloads](https://img.shields.io/nuget/dt/Rony.Net.svg?logo=nuget)](https://www.nuget.org/packages/Rony.Net)
[![.NET](https://img.shields.io/badge/.NET-netstandard2.1%20%7C%20net8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/github/license/archofthings/Rony.Net.svg)](LICENSE)

A simple TCP/UDP mock server for test projects that exercise .NET networking code.
Spin up a real TCP, TCP + SSL/TLS or UDP server in your test, tell it which request gets which response, and point your client at it.

## Why
While working on [Cimon.Net](https://github.com/MojtabaKiani/Cimon.Net), I realized that I couldn't mock sockets with existing libraries.
Faking sockets inside the project didn't really solve the problem, so I wrote this library and used it in Cimon.Net.

## Install
With the [NuGet Package Manager Console](https://www.nuget.org/packages/Rony.Net):
```console
Install-Package Rony.Net
```
Or with the .NET CLI:
```console
dotnet add package Rony.Net
```
The package targets `netstandard2.1` and `net8.0`, so it works with .NET Core 3.x and every later .NET version.

## Quick start
```csharp
using Rony;            // GetBytes() / GetString() helpers
using Rony.Listeners;  // TcpServer, TcpServerSsl, UdpServer, MessageFraming
using Rony.Net;        // MockServer, Times

using var server = new MockServer(new TcpServer(0));   // 0 = pick a free port
server.Mock.Send("PING").Receive("PONG");
server.Start();

using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, server.Port);
var stream = client.GetStream();
await stream.WriteAsync("PING".GetBytes());
var buffer = new byte[1024];
var read = await stream.ReadAsync(buffer);
// buffer[..read] is "PONG"

server.Mock.Verify("PING", Times.Once());
```
`GetBytes()` and `GetString()` are UTF-8 extension methods.

## Servers
Rony.Net provides three kinds of server. Address, port and other settings are set through the constructors.

### TCP
```csharp
using var tcpServer = new MockServer(new TcpServer(3000));
tcpServer.Start();
```
Connections stay open, so a client can send any number of requests over one connection.
Each connection is handled independently, so a slow or silent client never blocks the others.

### TCP with SSL/TLS
```csharp
using var tcpSslServer = new MockServer(new TcpServerSsl(4000, certificate, SslProtocols.None));
tcpSslServer.Start();
```
`certificate` is an `X509Certificate` with a private key, for example one loaded from a `.pfx` file or created on the fly
(see [`TestCertificate.cs`](tests/Rony.FunctionalTests/TestCertificate.cs)).
You can also pass the subject name of an installed certificate instead. It is looked up in the `CurrentUser` and
`LocalMachine` "My" stores, and you need read permission on its private key.
`SslProtocols.None` lets the operating system choose the protocol version; set a specific one if you need to.

### UDP
```csharp
using var udpServer = new MockServer(new UdpServer(5000));
udpServer.Start();
```

### Automatic ports
Hard-coded ports cause clashes when tests run in parallel. Pass port `0` to let the operating system pick a free port,
then read it from `server.Port` after `Start()`. The server keeps that port if you stop and start it again.
```csharp
using var server = new MockServer(new UdpServer("127.0.0.1", 0));
server.Start();
var port = server.Port;
```

### TCP options
Set these before `Start()`:
```csharp
new TcpServer(0)
{
    Framing = MessageFraming.Delimiter("\r\n"),  // how the stream is split into messages
    KeepAlive = false                            // close the connection after every response
};
```

| Framing | Requests | Responses |
|---|---|---|
| `MessageFraming.None` (default) | Everything that arrives in one burst is one request | Sent as configured |
| `MessageFraming.Delimiter("\n")` | Split on the delimiter, which is removed before matching | The delimiter is appended |
| `MessageFraming.LengthPrefix(2)` | Read a 1, 2 or 4-byte length (big-endian by default), then that many bytes | The length prefix is added |

With framing, several requests in one packet, or one request split over several packets, are handled correctly.
For other protocols, implement `IMessageFraming`. Framing applies to TCP and TCP + SSL/TLS; UDP datagrams are always one message each.

## Configuring responses
Use `server.Mock` to say which request gets which response:
```csharp
server.Mock.Send("Test String").Receive("Test Response");
server.Mock.Send(new byte[] { 1, 2, 3 }).Receive(new byte[] { 3, 2, 1 });
server.Mock.Send("abcd").Receive(x => x.ToUpper());
server.Mock.Send(new byte[] { 0xFF, 0x01 }).Receive(x => x.Reverse().ToArray());
```
Requests are matched on their exact bytes, so binary protocols work as well as text ones.

### Patterns and predicates
For requests that contain timestamps, IDs or other changing parts:
```csharp
server.Mock.Send(new Regex(@"^LOGIN \w+$")).Receive("WELCOME");
server.Mock.SendMatching(request => request.StartsWith("GET ")).Receive("200 OK");
server.Mock.SendMatchingBytes(request => request[0] == 0x02).Receive(new byte[] { 0x06 });
```

### Matching any request
Use an empty request to reply to anything that isn't configured otherwise:
```csharp
server.Mock.Send("").Receive("Test Response");
```

### Sequences
Return different responses each time the same request arrives. The last response repeats once the sequence ends:
```csharp
server.Mock.Send("status").Receive("busy").Then("busy").Then("ready");
// busy, busy, ready, ready, ...
```

### Simulating failures
Test timeouts, retries and error handling:
```csharp
server.Mock.Send("slow").Receive("done").After(TimeSpan.FromSeconds(2));   // delayed response
server.Mock.Send("quit").Receive("bye").AndDisconnect();                   // reply, then close the connection
server.Mock.Send("crash").Disconnect();                                    // close without replying
server.Mock.Send("ignored").NoReply();                                     // never reply, keep the connection open
server.Mock.Send("flaky").Disconnect().Then("ok");                          // fail once, then succeed
```
Responses on one connection always arrive in the order of their requests, even when some are delayed.
`Disconnect()` only applies to TCP; for UDP it behaves like `NoReply()`.

### Matching rules
- An exact request wins over patterns and predicates (checked in the order you added them), which win over the "any request" config.
- If nothing matches, the server sends an empty response and closes the TCP connection. Over UDP, it sends an empty datagram.
- If a `Receive(...)` function throws, the server sends an empty response.
- Configuring the same exact request twice throws an `ArgumentException`.
- `server.Mock` can be changed before or after `server.Start()`, and from multiple threads.
- `server.Mock.Reset()` removes every configured response and recorded request.

## Checking what the client sent
Every request is recorded, whether or not it matched:
```csharp
server.Mock.Verify("PING");                                   // at least once
server.Mock.Verify("PING", Times.Exactly(3));
server.Mock.Verify(r => r.BodyString.StartsWith("AUTH"), Times.Once());
server.Mock.VerifyAllRequestsMatched();                       // strict mode: fail on unexpected requests

foreach (var request in server.ReceivedRequests)
    Console.WriteLine($"{request.Timestamp} {request.RemoteEndPoint}: {request.BodyString} (matched: {request.Matched})");
```
A failed check throws `MockVerificationException`, listing every request the server received.

### Waiting for a request
Instead of `Thread.Sleep`, wait until the server has seen a request. This includes requests that arrived before you started waiting.
```csharp
await server.Mock.WaitForRequestAsync("HEARTBEAT", TimeSpan.FromSeconds(2));
await server.Mock.WaitForRequestAsync(r => r.Body.Length > 100);
var requests = await server.Mock.WaitForRequestsAsync(count: 3);
```
The timeout defaults to 5 seconds. When it expires, a `TimeoutException` lists what was received.
Use `server.Mock.ClearReceivedRequests()` to start over.

For more examples, see the [test projects](tests), especially
[`MockServerFeatureTests.cs`](tests/Rony.FunctionalTests/MockServerFeatureTests.cs).

## Upgrading
See the [changelog](CHANGELOG.md). The main change in 0.3.0: TCP connections now stay open after a response.
If your client reads until the server closes the connection, set `KeepAlive = false`.

## Build and test
You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or later. Visual Studio 2022, Rider and VS Code all work.
```console
dotnet build
dotnet test
```
The SSL/TLS tests create a self-signed `localhost` certificate at runtime, so you don't need to install one.
CI runs the build and tests on Linux and Windows for every push and pull request.

## License
[MIT](LICENSE)
