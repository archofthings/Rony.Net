# SSL and TLS

`TcpServerSsl` works like `TcpServer`, with a TLS handshake when each client connects. Persistent connections,
framing, matching and everything else behave the same.

## With a certificate object (recommended)
```csharp
using var certificate = TestCertificate.CreateSelfSigned();
using var server = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.None));
server.Mock.Send("hello").Receive("secure world");
server.Start();

using var client = await TcpTestClient.ConnectSslAsync(server.Port, certificate);
Assert.Equal("secure world", await client.SendAndReceiveAsync("hello"));
```

The certificate needs a private key. It can come from:
- A certificate created during the test (below). Nothing is added to a certificate store; the .NET runtime may keep the private key in a temporary keychain (macOS) or key container (Windows).
- A file: `new X509Certificate2("server.pfx", "password")`.
- Your own certificate store.

`SslProtocols.None` lets the operating system choose the best protocol. To test a specific version:
```csharp
new TcpServerSsl(0, certificate, SslProtocols.Tls12)
```

## Creating a certificate in the test
`TestCertificate.CreateSelfSigned()` creates a self-signed certificate for `localhost`. Nothing is added to a certificate store; the .NET runtime may keep the private key in a temporary keychain (macOS) or key container (Windows).
The name must be a plain host name or IP address, because it is used as the common name without escaping:

```csharp
using var certificate = TestCertificate.CreateSelfSigned();               // CN=localhost
using var clientCertificate = TestCertificate.CreateSelfSigned("my-client");   // CN=my-client
```

The certificate has a private key, is valid for seven days and can be used on a server and on a client (server and
client authentication). Its subject alternative names are the DNS name you passed, or the IP address if you passed one;
`localhost` also gets `127.0.0.1` and `::1`. Each call creates a new certificate, and you dispose it. The certificate
is exported and re-imported, so its private key works with `SslStream` on every OS (Windows needs this).

## Trusting the certificate in your client
A self-signed certificate isn't trusted by default, so the client's validation fails. In tests, trust exactly that
certificate instead of turning validation off:

```csharp
var ssl = new SslStream(tcpClient.GetStream(), false,
    (_, certificate, _, _) => certificate?.GetCertHashString() == serverCertificate.GetCertHashString());
await ssl.AuthenticateAsClientAsync("localhost");
```

If your client class creates its own `SslStream`, give it a way to accept a validation callback, or a set of trusted
certificates, so tests can pass this in.

## With an installed certificate
You can pass a subject name instead of a certificate object:
```csharp
new TcpServerSsl(4000, "localhost", SslProtocols.None)
```
The certificate is looked up in the `CurrentUser` "My" store first, then `LocalMachine`. The first certificate with that
subject name and a private key you can read is used. The lookup happens when the first client connects.
If no certificate is found, the handshake fails and the client sees the connection close; see
[Troubleshooting](Troubleshooting#the-tls-handshake-fails).

Passing a certificate object is more reliable, because it works on every machine and CI agent without setup.

## Failing the handshake
To test how your client reacts to a server whose TLS handshake fails, set `FailHandshake`. The server waits for the
client's hello, answers with a fatal `handshake_failure` alert and closes the connection:

```csharp
using var certificate = TestCertificate.CreateSelfSigned();
using var server = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.Tls12) { FailHandshake = true });
server.Start();

// AuthenticationException or IOException, depending on the platform.
await Assert.ThrowsAnyAsync<Exception>(() => TcpTestClient.ConnectSslAsync(server.Port, certificate));
server.Should().HaveAcceptedConnections(Times.Never());
```

The failed connection never appears in `server.Connections`; it is [logged](Logging-and-Diagnostics) as a failed
connection. You can change `FailHandshake` while the server runs; it applies to new connections.

## Mutual TLS (client certificates)
Set `RequireClientCertificate` to make the server ask for a client certificate. A client that sends none fails the
handshake. A presented certificate is accepted whatever its chain or trust errors (test certificates are self-signed),
unless you set `ClientCertificateValidator` and it returns `false` (or throws):

```csharp
using var serverCertificate = TestCertificate.CreateSelfSigned();
using var clientCertificate = TestCertificate.CreateSelfSigned("my-client");
using var server = new MockServer(new TcpServerSsl(0, serverCertificate, SslProtocols.Tls12) { RequireClientCertificate = true });
server.Mock.Send("hello").Receive("secure world");
server.Start();

using var client = await TcpTestClient.ConnectSslAsync(server.Port, serverCertificate, clientCertificate);
Assert.Equal("secure world", await client.SendAndReceiveAsync("hello"));
```

To accept only some certificates:
```csharp
using var serverCertificate = TestCertificate.CreateSelfSigned();
using var clientCertificate = TestCertificate.CreateSelfSigned("my-client");
using var server = new MockServer(new TcpServerSsl(0, serverCertificate, SslProtocols.Tls12)
{
    RequireClientCertificate = true,
    ClientCertificateValidator = certificate => certificate.Subject == "CN=my-client"
});
```

A rejected client behaves like any [failed handshake](#failing-the-handshake): it never appears in `server.Connections`,
and `ConnectionFailed` is raised, so the [log](Logging-and-Diagnostics) shows why ("the client sent no certificate" or
"the client certificate was rejected by ClientCertificateValidator"):

```csharp
using var serverCertificate = TestCertificate.CreateSelfSigned();
var listener = new TcpServerSsl(0, serverCertificate, SslProtocols.Tls12) { RequireClientCertificate = true };
var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
listener.ConnectionFailed += (_, error) => failed.TrySetResult(error);
using var server = new MockServer(listener);
server.Start();

// Depending on the OS and TLS version, the client sees the rejection while connecting or on its first read.
await Record.ExceptionAsync(async () =>
{
    using var client = await TcpTestClient.ConnectSslAsync(server.Port, serverCertificate);
});

var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
Assert.Contains("no certificate", error.Message);
server.Should().HaveAcceptedConnections(Times.Never());
```

Depending on the OS and TLS version, the client sees the rejection while connecting or only on its first read or write
(an `IOException` or a closed connection). Tests should not rely on which. Wait for the rejection on the server side
instead (the failed connection is logged, and the listener's `ConnectionFailed` event is raised), not with a delay.

Both properties can be changed while the server runs; they apply to new connections. Revocation is not checked.

## Checking protocol, server name and client certificate
`connection.Tls` describes the TLS handshake of a [connection](Connections-and-Push): `Protocol` (the negotiated
`SslProtocols`), `ServerName` (the SNI host name the client sent, `null` if none) and `ClientCertificate` (`null` if the
client presented none; a copy that stays readable after the connection closed). `Tls` is `null` on a connection without
TLS. The details are also in the log line when a client connects:
`#1 connected from 127.0.0.1:50125 (Tls12, server name localhost, client certificate CN=my-client)`.

```csharp
var connection = await server.WaitForConnectionAsync();
connection.Should().HaveUsedTls(SslProtocols.Tls12)
    .And.HaveServerName("localhost")
    .And.HavePresentedClientCertificate(clientCertificate);
```

| Assertion | Checks |
|---|---|
| `HaveUsedTls(SslProtocols protocol)` | The negotiated protocol |
| `HaveServerName(string serverName)` | The SNI host name, ignoring case |
| `HavePresentedClientCertificate()` | The client presented a certificate |
| `HavePresentedClientCertificate(X509Certificate certificate)` | The client presented this certificate (compared by hash) |

On a connection without TLS each of them fails, saying the connection did not use TLS. With `SslProtocols.None` the
negotiated protocol depends on the OS, so pin the server to a version when you assert it. A custom listener
reports these details by implementing [`ITlsListener`](Custom-Listeners#tls-details-in-a-custom-listener).

## Constructors
```csharp
new TcpServerSsl(port, certificate, protocol)
new TcpServerSsl("0.0.0.0", port, certificate, protocol)
new TcpServerSsl(IPAddress.Loopback, port, certificate, protocol)
new TcpServerSsl(port, "subject name", protocol)
new TcpServerSsl("0.0.0.0", port, "subject name", protocol)
new TcpServerSsl(IPAddress.Loopback, port, "subject name", protocol)
```

Runnable code: [`ServerSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ServerSamples.cs)
