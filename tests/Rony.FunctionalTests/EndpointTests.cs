using Rony.Interfaces;
using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Rony.FunctionalTests
{
    /// <summary>IPv6, dual-stack and Unix domain socket endpoints over real sockets.</summary>
    public class EndpointTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private static async Task<string> TcpRoundTripAsync(IPAddress address, int port, string request)
        {
            using var client = new TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port);
            using var stream = client.GetStream();
            return await ExchangeAsync(stream, request);
        }

        private static async Task<string> ExchangeAsync(Stream stream, string request)
        {
            using var cts = new CancellationTokenSource(Timeout);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), cts.Token);
            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer, cts.Token);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        private static async Task<string> UdpRoundTripAsync(IPAddress address, int port, string request)
        {
            using var client = new UdpClient(address.AddressFamily);
            var data = Encoding.UTF8.GetBytes(request);
            await client.SendAsync(data, data.Length, new IPEndPoint(address, port));
            var response = await client.ReceiveAsync().WaitAsync(Timeout);
            return Encoding.UTF8.GetString(response.Buffer);
        }

        [Theory]
        [InlineData("tcp")]
        [InlineData("tls")]
        [InlineData("udp")]
        public async Task IPv6_Loopback_Should_Round_Trip(string kind)
        {
            if (!Socket.OSSupportsIPv6) return; // no dynamic skip in xUnit v2

            //Arrange
            IListener listener = kind switch
            {
                "tcp" => new TcpServer(IPAddress.IPv6Loopback, 0),
                "tls" => new TcpServerSsl(IPAddress.IPv6Loopback, 0, SharedCertificate.Instance, SslProtocols.None),
                _ => new UdpServer(new IPEndPoint(IPAddress.IPv6Loopback, 0))
            };
            using var server = new MockServer(listener);
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            string response;
            if (kind == "tls")
            {
                using var client = new TcpClient(AddressFamily.InterNetworkV6);
                await client.ConnectAsync(IPAddress.IPv6Loopback, server.Port);
                using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync("localhost");
                response = await ExchangeAsync(ssl, "ping");
            }
            else
            {
                response = kind == "tcp"
                    ? await TcpRoundTripAsync(IPAddress.IPv6Loopback, server.Port, "ping")
                    : await UdpRoundTripAsync(IPAddress.IPv6Loopback, server.Port, "ping");
            }

            //Assert
            Assert.Equal("pong", response);
            Assert.Equal(IPAddress.IPv6Loopback, server.Address);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DualMode_Should_Answer_IPv4_And_IPv6_Clients(bool udp)
        {
            if (!Socket.OSSupportsIPv6) return; // no dynamic skip in xUnit v2

            //Arrange
            IListener listener = udp
                ? new UdpServer(new IPEndPoint(IPAddress.IPv6Any, 0), true)
                : new TcpServer(IPAddress.IPv6Any, 0) { DualMode = true };
            using var server = new MockServer(listener);
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            var viaIPv4 = udp ? await UdpRoundTripAsync(IPAddress.Loopback, server.Port, "ping") : await TcpRoundTripAsync(IPAddress.Loopback, server.Port, "ping");
            var viaIPv6 = udp ? await UdpRoundTripAsync(IPAddress.IPv6Loopback, server.Port, "ping") : await TcpRoundTripAsync(IPAddress.IPv6Loopback, server.Port, "ping");

            //Assert
            Assert.Equal("pong", viaIPv4);
            Assert.Equal("pong", viaIPv6);
        }

        [Fact]
        public void DualMode_Should_Need_An_IPv6_Address()
        {
            using var tcp = new MockServer(new TcpServer(IPAddress.Loopback, 0) { DualMode = true });
            Assert.Throws<InvalidOperationException>(() => tcp.Start());
            Assert.Throws<ArgumentException>(() => new UdpServer(new IPEndPoint(IPAddress.Loopback, 0), true));
        }

        private sealed class UnixClient : IDisposable
        {
            private readonly Socket _socket;
            private readonly NetworkStream _stream;

            public UnixClient(string path)
            {
                _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    _socket.Connect(new UnixDomainSocketEndPoint(path));
                }
                catch
                {
                    _socket.Dispose();
                    throw;
                }
                _stream = new NetworkStream(_socket, true);
            }

            public async Task SendAsync(string line)
            {
                using var cts = new CancellationTokenSource(Timeout);
                await _stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cts.Token);
            }

            /// <summary>Reads one line, or null when the connection ended.</summary>
            public async Task<string> ReadLineAsync()
            {
                using var cts = new CancellationTokenSource(Timeout);
                var builder = new StringBuilder();
                var one = new byte[1];
                try
                {
                    while (true)
                    {
                        if (await _stream.ReadAsync(one, cts.Token) == 0) return builder.Length == 0 ? null : builder.ToString();
                        if (one[0] == '\n') return builder.ToString();
                        builder.Append((char)one[0]);
                    }
                }
                catch (IOException)
                {
                    return null; // a reset connection has ended too
                }
            }

            public void Dispose() => _stream.Dispose();
        }

        private static (MockServer Server, UnixSocketServer Listener) UnixServer(Action<MockServer> configure = null, string path = null)
        {
            var listener = path == null ? new UnixSocketServer() : new UnixSocketServer(path);
            listener.Framing = MessageFraming.Delimiter("\n");
            var server = new MockServer(listener);
            configure?.Invoke(server);
            return (server, listener);
        }

        [Fact]
        public async Task UnixSocket_Should_Answer_Greet_Push_And_Track_The_Connection()
        {
            if (!Socket.OSSupportsUnixDomainSockets) return; // no dynamic skip in xUnit v2

            //Arrange
            var (server, listener) = UnixServer(s =>
            {
                s.Mock.OnConnect().Receive("hello");
                s.Mock.Send("ping").Receive("pong");
            });
            using var _ = server;
            server.Start();

            //Act
            using var client = new UnixClient(listener.Path);
            var connection = await server.WaitForConnectionAsync(Timeout);
            var greeting = await client.ReadLineAsync();
            await client.SendAsync("ping");
            var response = await client.ReadLineAsync();
            await connection.SendAsync("pushed");
            var pushed = await client.ReadLineAsync();

            //Assert
            Assert.Equal("hello", greeting);
            Assert.Equal("pong", response);
            Assert.Equal("pushed", pushed);
            Assert.True(File.Exists(listener.Path));
            Assert.Single(server.OpenConnections);
            Assert.Equal(IPAddress.None, server.Address);
            Assert.Equal(0, server.Port);
            Assert.False(string.IsNullOrEmpty(connection.ToString()));
        }

        [Fact]
        public async Task UnixSocket_File_Should_Follow_The_Server_Lifecycle_And_Restart_On_The_Same_Path()
        {
            if (!Socket.OSSupportsUnixDomainSockets) return; // no dynamic skip in xUnit v2

            //Arrange
            var (server, listener) = UnixServer(s => s.Mock.Send("ping").Receive("pong"));
            var path = listener.Path;
            using (server)
            {
                //Act
                server.Start();
                var existed = File.Exists(path);
                server.Stop();
                var existsAfterStop = File.Exists(path);
                server.Start();
                string response;
                using (var client = new UnixClient(path))
                {
                    await client.SendAsync("ping");
                    response = await client.ReadLineAsync();
                }

                //Assert
                Assert.True(existed);
                Assert.False(existsAfterStop);
                Assert.Equal("pong", response);
            }

            Assert.False(File.Exists(path));
        }

        [Fact]
        public void UnixSocket_Should_Not_Replace_A_File_It_Did_Not_Create()
        {
            if (!Socket.OSSupportsUnixDomainSockets) return; // no dynamic skip in xUnit v2

            //Arrange
            var path = Path.Combine(Path.GetTempPath(), "rony-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock");
            File.WriteAllText(path, "mine");
            try
            {
                using var server = new MockServer(new UnixSocketServer(path));

                //Act + Assert
                Assert.Throws<SocketException>(() => server.Start());
                Assert.Equal("mine", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void UnixSocket_Should_Reject_An_Empty_Path(string path)
        {
            Assert.Throws<ArgumentException>(() => new UnixSocketServer(path));
        }

        [Fact]
        public async Task UnixSocket_Should_Refuse_And_Accept_Connections_On_The_Same_Path()
        {
            if (!Socket.OSSupportsUnixDomainSockets) return; // no dynamic skip in xUnit v2

            //Arrange
            var (server, listener) = UnixServer(s => s.Mock.Send("ping").Receive("pong"));
            using var _ = server;
            server.Start();

            //Act
            server.RefuseConnections();
            var refused = Assert.ThrowsAny<SocketException>(() => new UnixClient(listener.Path));
            server.AcceptConnections();
            using var client = new UnixClient(listener.Path);
            await client.SendAsync("ping");

            //Assert
            Assert.NotNull(refused);
            Assert.Equal("pong", await client.ReadLineAsync());
        }

        [Fact]
        public async Task UnixSocket_Reset_Should_Close_The_Connection_Without_Throwing()
        {
            if (!Socket.OSSupportsUnixDomainSockets) return; // no dynamic skip in xUnit v2

            //Arrange
            var (server, listener) = UnixServer();
            using var _ = server;
            server.Start();
            using var client = new UnixClient(listener.Path);
            var connection = await server.WaitForConnectionAsync(Timeout);

            //Act
            await connection.ResetAsync();
            await connection.WaitForCloseAsync(Timeout);

            //Assert
            Assert.False(connection.IsOpen);
            Assert.Null(await client.ReadLineAsync());
        }

        [Fact]
        public async Task UnixSocket_Chunked_Response_Should_Arrive_Intact()
        {
            if (!Socket.OSSupportsUnixDomainSockets) return; // no dynamic skip in xUnit v2

            //Arrange
            var (server, listener) = UnixServer(s => s.Mock.Send("ping").Receive("abcdefghij").InChunks(3));
            using var _ = server;
            server.Start();
            using var client = new UnixClient(listener.Path);

            //Act
            await client.SendAsync("ping");

            //Assert
            Assert.Equal("abcdefghij", await client.ReadLineAsync());
        }
    }
}
