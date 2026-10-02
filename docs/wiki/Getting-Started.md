# Getting Started

## Install
```console
dotnet add package Rony.Net
```
Or in the Package Manager Console: `Install-Package Rony.Net`.

## Namespaces
```csharp
using Rony;            // GetBytes() / GetString() UTF-8 helpers
using Rony.Listeners;  // TcpServer, TcpServerSsl, UdpServer, MessageFraming
using Rony.Net;        // MockServer, Times, MockVerificationException
using Rony.Models;     // ReceivedRequest (only if you name the type)
```

## Your first test
Every test follows the same three steps: create and configure a server, run your client against it, then check the results.

```csharp
[Fact]
public async Task First_test_with_a_plain_TcpClient()
{
    // 1. Create a server on a free port and tell it what to answer.
    using var server = new MockServer(new TcpServer(0));
    server.Mock.Send("PING").Receive("PONG");
    server.Start();

    // 2. Talk to it with any client, here a plain TcpClient.
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, server.Port);
    var stream = client.GetStream();
    await stream.WriteAsync("PING".GetBytes());

    var buffer = new byte[1024];
    var read = await stream.ReadAsync(buffer);

    // 3. Check the response, and what the client sent.
    Assert.Equal("PONG", buffer[..read].GetString());
    server.Should().HaveReceived("PING", Times.Once());
}
```

Some points to note:
- `new TcpServer(0)` asks the operating system for a free port. Read it from `server.Port` after `Start()`.
  See [Ports and Lifecycle](Ports-and-Lifecycle).
- `using var server` stops the server at the end of the test.
- In a real test, step 2 is *your* code: the client class you want to test. See [Recipes](Recipes).

## The same with UDP
```csharp
[Fact]
public async Task First_test_with_UDP()
{
    using var server = new MockServer(new UdpServer("127.0.0.1", 0));
    server.Mock.Send("PING").Receive("PONG");
    server.Start();

    using var client = new UdpClient();
    var request = "PING".GetBytes();
    await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, server.Port));
    var response = await client.ReceiveAsync();

    Assert.Equal("PONG", response.Buffer.GetString());
}
```

## The helper used in this wiki
To keep the examples focused on the server, most pages use a small client helper from the samples project:
[`TestClients.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/TestClients.cs).
Copy it into your own test project if you like. Its methods are:

| Method | What it does |
|---|---|
| `TcpTestClient.ConnectAsync(port)` | Connects to `127.0.0.1:port` |
| `TcpTestClient.ConnectSslAsync(port, certificate)` | Connects with TLS, trusting exactly that certificate |
| `SendAsync(string or byte[])` | Writes to the connection |
| `ReceiveAsync()` / `ReceiveBytesAsync()` | Reads whatever arrives next (empty when the server closed the connection) |
| `ReceiveExactlyAsync(count)` | Reads exactly `count` bytes |
| `SendAndReceiveAsync(request)` | Sends, then reads the response as text |
| `ReadToEndAsync()` | Reads until the server closes the connection |
| `UdpTestClient.SendAndReceiveAsync(port, request)` | Sends one datagram and returns the reply |

Every read gives up after 5 seconds, so a wrong test fails instead of hanging. With the helper, the first test becomes:

```csharp
using var server = new MockServer(new TcpServer(0));
server.Mock.Send("PING").Receive("PONG");
server.Start();

using var client = await TcpTestClient.ConnectAsync(server.Port);

Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));
```

## Next steps
- [Servers](Servers) and [SSL and TLS](SSL-and-TLS): pick a transport.
- [Configuring Responses](Configuring-Responses): everything `Send(...).Receive(...)` can do.
- [Verifying Requests](Verifying-Requests): assert on what your client sent.
