[![NuGet version](https://badge.fury.io/nu/Rony.Net.svg)](https://badge.fury.io/nu/Rony.Net)

# Rony.Net
A simple TCP/UDP mock server for using in test projects which test .Net core based projects.

## Problem
When I was working on [Cimon.Net](https://github.com/MojtabaKiani/Cimon.Net) project, I realized that I can't mock sockets with existed libraries, So I changed my project to create fake sockets. But the problem still existed, So I started to write this project and finally I used this in Cimon.Net. 

## Install
You can install `Rony.Net` with [NuGet Package Manager Console](https://www.nuget.org/packages/Rony.Net):
```console
Install-Package Rony.Net
```
Or via the .NET command-line interface:
```console
dotnet add package Rony.Net
```
The package targets `netstandard2.1` and `net8.0`, so it works with .NET Core 3.x and every later .NET version.

## Usage
With Rony.Net you can create 3 types of Server :
* TCP Server
* TCP Server with SSL/TLS support
* UDP Server

You can create and run mock servers as below. Port, IP and other settings are configurable via constructors :
```csharp
using var tcpServer = new MockServer(new TcpServer(3000));
tcpServer.Start();
```
```csharp
using var tcpSslServer = new MockServer(new TcpServerSsl(4000, certificate, SslProtocols.None));
tcpSslServer.Start();
```
*`certificate` is an `X509Certificate` with a private key, for example one you load from a `.pfx` file or create on the fly
(see `tests/Rony.FunctionalTests/TestCertificate.cs`). You can also pass the subject name of an installed certificate instead;
it is looked up in the `CurrentUser` and `LocalMachine` "My" stores, and you need read permission on its private key.
You can set `SslProtocols` based on your requirements; `SslProtocols.None` lets the operating system choose.*
```csharp
using var udpServer = new MockServer(new UdpServer(5000));
udpServer.Start();
```

Then you can use a normal client to connect and sending request to them, just like below :
```csharp
using var client = new TcpClient();
await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), 3000);
```
```csharp
using var client = new UdpClient();
client.Connect(IPAddress.Parse("127.0.0.1"), 5000);
```

You can use `mockServer.Mock` to manage Send/Receive data, then server will return configured data, based on sent data:
```csharp
mockServer.Mock.Send("Test String").Receive("Test Response");
mockServer.Mock.Send(new byte[] { 1, 2, 3 }).Receive(new byte[] { 3, 2, 1 });
mockServer.Mock.Send("abcd").Receive(x => x.ToUpper());
mockServer.Mock.Send(new byte[] { 0xFF, 0x01 }).Receive(x => x.Reverse().ToArray());
```
Requests are matched on their exact bytes, so binary protocols work as well as text ones.

An important option in using `mockServer.Mock` is adding `Any` request to it, then it will reply to any unconfigured request based on this config. You can configure
the server for this option by using an empty string in `Send()` method, just like below :
```csharp
mockServer.Mock.Send("").Receive("Test Response");
```
A request with an exact match always wins over the `Any` config. If nothing matches, the server sends an empty response
(TCP connections are closed without data). The same happens when a `Receive(...)` function throws.

You can use `mockServer.Mock` either before or after `mockServer.Start()`, and from multiple threads. For more details please check Test projects.

## Compile
You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or later. Any editor works; Visual Studio 2022, Rider and VS Code are all fine.
```console
dotnet build
```

## Running the tests
All tests use xUnit:
```console
dotnet test
```
The SSL/TLS tests create a self-signed `localhost` certificate at runtime, so no certificate needs to be installed.
