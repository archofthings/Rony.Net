using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Rony;

namespace Rony.Samples;

/// <summary>
/// A small TCP client used by the samples, so they can focus on the mock server.
/// Every read times out after 5 seconds instead of hanging a test.
/// </summary>
public sealed class TcpTestClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly IDisposable _client;
    private readonly Stream _stream;

    private TcpTestClient(IDisposable client, Stream stream)
    {
        _client = client;
        _stream = stream;
    }

    public static Task<TcpTestClient> ConnectAsync(int port) => ConnectAsync(IPAddress.Loopback, port);

    /// <summary>Connects to <paramref name="address"/>, for example <see cref="IPAddress.IPv6Loopback"/>.</summary>
    public static async Task<TcpTestClient> ConnectAsync(IPAddress address, int port)
    {
        var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, port);
        return new TcpTestClient(client, client.GetStream());
    }

    /// <summary>Connects to a Unix domain socket file.</summary>
    public static async Task<TcpTestClient> ConnectUnixAsync(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        return new TcpTestClient(socket, new NetworkStream(socket));
    }

    /// <summary>
    /// Connects with TLS, trusting exactly <paramref name="serverCertificate"/>, and presents
    /// <paramref name="clientCertificate"/> when given.
    /// </summary>
    public static async Task<TcpTestClient> ConnectSslAsync(int port, X509Certificate2 serverCertificate,
        X509Certificate2 clientCertificate = null)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var ssl = new SslStream(client.GetStream(), false,
            (_, certificate, _, _) => certificate?.GetCertHashString() == serverCertificate.GetCertHashString());
        var clientCertificates = clientCertificate == null ? null : new X509CertificateCollection { clientCertificate };
        await ssl.AuthenticateAsClientAsync("localhost", clientCertificates, SslProtocols.None, false);
        return new TcpTestClient(client, ssl);
    }

    public Task SendAsync(string text) => SendAsync(text.GetBytes());

    public async Task SendAsync(byte[] data) => await _stream.WriteAsync(data);

    /// <summary>Reads whatever arrives next, as text.</summary>
    public async Task<string> ReceiveAsync() => (await ReceiveBytesAsync()).GetString();

    /// <summary>Reads whatever arrives next. Returns an empty array when the server closed the connection.</summary>
    public async Task<byte[]> ReceiveBytesAsync()
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(Timeout);
        var read = await _stream.ReadAsync(buffer, timeout.Token);
        return buffer[..read];
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, even if they arrive in several packets.</summary>
    public async Task<byte[]> ReceiveExactlyAsync(int count)
    {
        var buffer = new byte[count];
        using var timeout = new CancellationTokenSource(Timeout);
        await _stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    public async Task<string> SendAndReceiveAsync(string request)
    {
        await SendAsync(request);
        return await ReceiveAsync();
    }

    /// <summary>Reads until the server closes the connection.</summary>
    public async Task<string> ReadToEndAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var received = new MemoryStream();
        try
        {
            await _stream.CopyToAsync(received, timeout.Token);
        }
        catch (IOException)
        {
            // A reset connection is closed too.
        }
        return received.ToArray().GetString();
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}

public static class UdpTestClient
{
    /// <summary>Sends one datagram and waits up to 5 seconds for the reply.</summary>
    public static Task<string> SendAndReceiveAsync(int port, string request) => SendAndReceiveAsync(IPAddress.Loopback, port, request);

    /// <summary>Sends one datagram to <paramref name="address"/> and waits up to 5 seconds for the reply.</summary>
    public static async Task<string> SendAndReceiveAsync(IPAddress address, int port, string request)
    {
        using var client = new UdpClient(address.AddressFamily);
        var data = request.GetBytes();
        await client.SendAsync(data, data.Length, new IPEndPoint(address, port));
        var response = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return response.Buffer.GetString();
    }
}
