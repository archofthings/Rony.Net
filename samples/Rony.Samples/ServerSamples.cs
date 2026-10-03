using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Servers, SSL-TLS
public class ServerSamples
{
    [Fact]
    public async Task Tcp_server()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("hello").Receive("world");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("world", await client.SendAndReceiveAsync("hello"));
    }

    [Fact]
    public async Task Udp_server()
    {
        using var server = new MockServer(new UdpServer("127.0.0.1", 0));
        server.Mock.Send("hello").Receive("world");
        server.Start();

        Assert.Equal("world", await UdpTestClient.SendAndReceiveAsync(server.Port, "hello"));
    }

    [Fact]
    public async Task IPv6_loopback_server()   // Servers
    {
        if (!Socket.OSSupportsIPv6) return;   // no dynamic skip in xUnit v2

        using var server = new MockServer(new TcpServer(IPAddress.IPv6Loopback, 0));
        server.Mock.Send("hello").Receive("world");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(IPAddress.IPv6Loopback, server.Port);
        Assert.Equal("world", await client.SendAndReceiveAsync("hello"));
        server.Should().HaveReceived("hello", Times.Once());
    }

    [Fact]
    public async Task Dual_mode_tcp_server()   // Servers
    {
        if (!Socket.OSSupportsIPv6) return;   // no dynamic skip in xUnit v2

        using var server = new MockServer(new TcpServer(IPAddress.IPv6Any, 0) { DualMode = true });
        server.Mock.Send("hello").Receive("world");
        server.Start();

        using var viaIPv4 = await TcpTestClient.ConnectAsync(IPAddress.Loopback, server.Port);
        using var viaIPv6 = await TcpTestClient.ConnectAsync(IPAddress.IPv6Loopback, server.Port);
        Assert.Equal("world", await viaIPv4.SendAndReceiveAsync("hello"));
        Assert.Equal("world", await viaIPv6.SendAndReceiveAsync("hello"));
        server.Should().HaveReceived("hello", Times.Exactly(2));
    }

    [Fact]
    public async Task Dual_mode_udp_server()   // Servers
    {
        if (!Socket.OSSupportsIPv6) return;   // no dynamic skip in xUnit v2

        using var server = new MockServer(new UdpServer(new IPEndPoint(IPAddress.IPv6Any, 0), dualMode: true));
        server.Mock.Send("hello").Receive("world");
        server.Start();

        Assert.Equal("world", await UdpTestClient.SendAndReceiveAsync(IPAddress.Loopback, server.Port, "hello"));
        Assert.Equal("world", await UdpTestClient.SendAndReceiveAsync(IPAddress.IPv6Loopback, server.Port, "hello"));
        server.Should().HaveReceived("hello", Times.Exactly(2));
    }

    [Fact]
    public async Task Unix_domain_socket_server()   // Servers
    {
        if (!Socket.OSSupportsUnixDomainSockets) return;   // no dynamic skip in xUnit v2

        var listener = new UnixSocketServer();   // a new socket file in the temp directory; read it from listener.Path
        using var server = new MockServer(listener);
        server.Mock.Send("hello").Receive("world");
        server.Start();

        using var client = await TcpTestClient.ConnectUnixAsync(listener.Path);
        Assert.Equal("world", await client.SendAndReceiveAsync("hello"));
        server.Should().HaveReceived("hello", Times.Once());
        Assert.True(File.Exists(listener.Path));
    }

    [Fact]
    public async Task Ssl_server_with_a_generated_certificate()
    {
        using var certificate = TestCertificate.CreateSelfSigned();
        using var server = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.None));
        server.Mock.Send("hello").Receive("secure world");
        server.Start();

        using var client = await TcpTestClient.ConnectSslAsync(server.Port, certificate);
        Assert.Equal("secure world", await client.SendAndReceiveAsync("hello"));
    }

    [Fact]
    public async Task Ssl_server_with_a_specific_protocol()
    {
        using var certificate = TestCertificate.CreateSelfSigned();
        using var server = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.Tls12));
        server.Mock.Send("hello").Receive("TLS 1.2");
        server.Start();

        using var client = await TcpTestClient.ConnectSslAsync(server.Port, certificate);
        Assert.Equal("TLS 1.2", await client.SendAndReceiveAsync("hello"));
    }

    [Fact]
    public async Task Mutual_tls_and_tls_details()   // SSL-and-TLS
    {
        using var serverCertificate = TestCertificate.CreateSelfSigned();
        using var clientCertificate = TestCertificate.CreateSelfSigned("my-client");
        using var server = new MockServer(new TcpServerSsl(0, serverCertificate, SslProtocols.Tls12) { RequireClientCertificate = true });
        server.Mock.Send("hello").Receive("secure world");
        server.Start();

        using var client = await TcpTestClient.ConnectSslAsync(server.Port, serverCertificate, clientCertificate);
        Assert.Equal("secure world", await client.SendAndReceiveAsync("hello"));

        var connection = await server.WaitForConnectionAsync();
        connection.Should().HaveUsedTls(SslProtocols.Tls12)
            .And.HaveServerName("localhost")
            .And.HavePresentedClientCertificate(clientCertificate);
    }

    [Fact]
    public async Task Mutual_tls_with_a_validator()   // SSL-and-TLS
    {
        using var serverCertificate = TestCertificate.CreateSelfSigned();
        using var clientCertificate = TestCertificate.CreateSelfSigned("my-client");
        using var server = new MockServer(new TcpServerSsl(0, serverCertificate, SslProtocols.Tls12)
        {
            RequireClientCertificate = true,
            ClientCertificateValidator = certificate => certificate.Subject == "CN=my-client"
        });
        server.Mock.Send("hello").Receive("secure world");
        server.Start();

        using var client = await TcpTestClient.ConnectSslAsync(server.Port, serverCertificate, clientCertificate);
        Assert.Equal("secure world", await client.SendAndReceiveAsync("hello"));
    }

    [Fact]
    public async Task Mutual_tls_rejects_a_client_without_a_certificate()   // SSL-and-TLS
    {
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
    }

    [Fact]
    public void Server_properties()
    {
        using var server = new MockServer(new TcpServer("127.0.0.1", 0));
        Assert.False(server.Active);

        server.Start();

        Assert.True(server.Active);
        Assert.Equal("127.0.0.1", server.Address.ToString());
        Assert.NotEqual(0, server.Port);
    }

    [Fact]
    public async Task Failing_the_tls_handshake()
    {
        using var certificate = TestCertificate.CreateSelfSigned();
        using var server = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.Tls12) { FailHandshake = true });
        server.Start();

        // AuthenticationException or IOException, depending on the platform.
        await Assert.ThrowsAnyAsync<Exception>(() => TcpTestClient.ConnectSslAsync(server.Port, certificate));
        server.Should().HaveAcceptedConnections(Times.Never());
    }
}
