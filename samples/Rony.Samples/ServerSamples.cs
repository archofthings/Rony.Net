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
    public async Task Ssl_server_with_a_generated_certificate()
    {
        using var certificate = TestCertificates.CreateSelfSigned();
        using var server = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.None));
        server.Mock.Send("hello").Receive("secure world");
        server.Start();

        using var client = await TcpTestClient.ConnectSslAsync(server.Port, certificate);
        Assert.Equal("secure world", await client.SendAndReceiveAsync("hello"));
    }

    [Fact]
    public async Task Ssl_server_with_a_specific_protocol()
    {
        using var certificate = TestCertificates.CreateSelfSigned();
        using var server = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.Tls12));
        server.Mock.Send("hello").Receive("TLS 1.2");
        server.Start();

        using var client = await TcpTestClient.ConnectSslAsync(server.Port, certificate);
        Assert.Equal("TLS 1.2", await client.SendAndReceiveAsync("hello"));
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
}
