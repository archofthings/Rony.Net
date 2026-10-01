# SSL and TLS

`TcpServerSsl` works like `TcpServer`, with a TLS handshake when each client connects. Persistent connections,
framing, matching and everything else behave the same.

## With a certificate object (recommended)
```csharp
using var certificate = TestCertificates.CreateSelfSigned();
using var server = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.None));
server.Mock.Send("hello").Receive("secure world");
server.Start();

using var client = await TcpTestClient.ConnectSslAsync(server.Port, certificate);
Assert.Equal("secure world", await client.SendAndReceiveAsync("hello"));
```

The certificate needs a private key. It can come from:
- A certificate created during the test (below). Nothing gets installed.
- A file: `new X509Certificate2("server.pfx", "password")`.
- Your own certificate store.

`SslProtocols.None` lets the operating system choose the best protocol. To test a specific version:
```csharp
new TcpServerSsl(0, certificate, SslProtocols.Tls12)
```

## Creating a certificate in the test
This creates a self-signed certificate for `localhost` that lasts one day:

```csharp
public static X509Certificate2 CreateSelfSigned()
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // server authentication
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName("localhost");
    request.CertificateExtensions.Add(names.Build());

    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    // Export and re-import so the private key works with SslStream on every OS (Windows needs this).
    return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
}
```

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

## Constructors
```csharp
new TcpServerSsl(port, certificate, protocol)
new TcpServerSsl("0.0.0.0", port, certificate, protocol)
new TcpServerSsl(IPAddress.Loopback, port, certificate, protocol)
new TcpServerSsl(port, "subject name", protocol)
new TcpServerSsl("0.0.0.0", port, "subject name", protocol)
new TcpServerSsl(IPAddress.Loopback, port, "subject name", protocol)
```

Runnable code: [`ServerSamples.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/ServerSamples.cs),
[`TestCertificates.cs`](https://github.com/archofthings/Rony.Net/blob/main/samples/Rony.Samples/TestCertificates.cs)
