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
using Rony.Listeners;  // TcpServer, TcpServerSsl, UdpServer
using Rony.Net;        // MockServer

using var server = new MockServer(new TcpServer(3000));
server.Mock.Send("PING").Receive("PONG");
server.Start();

using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Loopback, 3000);
var stream = client.GetStream();
await stream.WriteAsync("PING".GetBytes());
var buffer = new byte[1024];
var read = await stream.ReadAsync(buffer);
// buffer[..read] is "PONG"
```
`GetBytes()` and `GetString()` are UTF-8 extension methods.

## Servers
Rony.Net provides three kinds of server. Address, port and other settings are set through the constructors.

### TCP
```csharp
using var tcpServer = new MockServer(new TcpServer(3000));
tcpServer.Start();
```

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

Then connect with a normal client:
```csharp
using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), 3000);
```
```csharp
using var client = new UdpClient();
client.Connect(IPAddress.Parse("127.0.0.1"), 5000);
```

## Configuring responses
Use `server.Mock` to say which request gets which response:
```csharp
server.Mock.Send("Test String").Receive("Test Response");
server.Mock.Send(new byte[] { 1, 2, 3 }).Receive(new byte[] { 3, 2, 1 });
server.Mock.Send("abcd").Receive(x => x.ToUpper());
server.Mock.Send(new byte[] { 0xFF, 0x01 }).Receive(x => x.Reverse().ToArray());
```
Requests are matched on their exact bytes, so binary protocols work as well as text ones.

### Matching any request
Use an empty request to reply to anything that isn't configured explicitly:
```csharp
server.Mock.Send("").Receive("Test Response");
```

### Matching rules
- An exact match always wins over the "any request" config.
- If nothing matches, the server sends an empty response (a TCP connection is closed without data).
- If a `Receive(...)` function throws, the server also sends an empty response.
- Configuring the same request twice throws an `ArgumentException`.
- `server.Mock` can be used before or after `server.Start()`, and from multiple threads.

### Server behavior
- Each TCP connection carries one request and one response, then the server closes it.
- A misbehaving client (dropped connection, failed TLS handshake) doesn't stop the server.
- `Start()` and `Stop()` are safe to call more than once, and a stopped server can be started again.

For more examples, see the [test projects](tests).

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
