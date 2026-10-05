using Rony.Listeners;
using Rony.Net;
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Rony.FunctionalTests
{
    /// <summary>Servers created with <c>MockServer.FromJson</c> / <c>FromFile</c>, over real sockets.</summary>
    public class MockConfigurationTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        /// <summary>A line based client; every read times out after 5 seconds.</summary>
        private sealed class LineClient : IDisposable
        {
            private readonly IDisposable _owner;
            private readonly Stream _stream;
            private readonly StreamReader _reader;

            private LineClient(IDisposable owner, Stream stream)
            {
                _owner = owner;
                _stream = stream;
                _reader = new StreamReader(stream, Encoding.UTF8);
            }

            public static async Task<LineClient> ConnectAsync(int port, Func<X509Certificate, bool> acceptCertificate = null)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                Stream stream = client.GetStream();
                if (acceptCertificate != null)
                {
                    var ssl = new SslStream(stream, false, (_, certificate, _, _) => acceptCertificate(certificate));
                    await ssl.AuthenticateAsClientAsync("localhost");
                    stream = ssl;
                }
                return new LineClient(client, stream);
            }

            public static async Task<LineClient> ConnectUnixAsync(string path)
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                return new LineClient(socket, new NetworkStream(socket));
            }

            public async Task SendAsync(string line)
            {
                await _stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
                await _stream.FlushAsync();
            }

            /// <summary>The next line, or null once the connection ended.</summary>
            public async Task<string> ReadLineAsync()
            {
                try
                {
                    return await _reader.ReadLineAsync().WaitAsync(Timeout);
                }
                catch (IOException)
                {
                    return null;
                }
            }

            public async Task<string> ExchangeAsync(string line)
            {
                await SendAsync(line);
                return await ReadLineAsync();
            }

            public void Dispose()
            {
                _stream.Dispose();
                _owner.Dispose();
            }
        }

        [Fact]
        public async Task ReloadJson_Should_Change_The_Reply_On_An_Open_Connection()
        {
            using var server = MockServer.FromJson("""
                { "version": 1, "server": { "framing": { "type": "delimiter", "delimiter": "\n" } },
                  "rules": [ { "request": "PING", "reply": "old" } ] }
                """);
            server.Start();
            using var client = await LineClient.ConnectAsync(server.Port);
            Assert.Equal("old", await client.ExchangeAsync("PING"));

            server.ReloadJson("""{ "version": 1, "rules": [ { "request": "PING", "reply": "new" } ] }""");

            Assert.Equal("new", await client.ExchangeAsync("PING"));
            Assert.Single(server.Connections);
        }

        [Fact]
        public async Task Tcp_Configuration_Should_Greet_Match_Move_Through_States_And_Disconnect()
        {
            //Arrange
            using var server = MockServer.FromJson("""
                {
                  "version": 1,
                  "server": { "framing": { "type": "delimiter", "delimiter": "\n" } },
                  "onConnect": { "reply": "220 ready" },
                  "onUnmatched": { "reply": "ERR unknown" },
                  "rules": [
                    { "request": "PING", "reply": "PONG" },
                    { "match": "^HELLO (\\w+)$", "reply": "HI $1" },
                    { "json": { "type": "login", "user": { "name": "bob" } }, "reply": "{\"ok\":true}", "goTo": "authenticated" },
                    { "request": "LIST", "state": "authenticated",
                      "replies": [ { "reply": "a" }, { "reply": "b" }, { "disconnect": true } ] }
                  ]
                }
                """);
            server.Start();

            //Act
            using var client = await LineClient.ConnectAsync(server.Port);

            //Assert
            Assert.Equal("220 ready", await client.ReadLineAsync());
            Assert.Equal("PONG", await client.ExchangeAsync("PING"));
            Assert.Equal("HI bob", await client.ExchangeAsync("HELLO bob"));
            Assert.Equal("ERR unknown", await client.ExchangeAsync("LIST"));
            Assert.Equal("{\"ok\":true}", await client.ExchangeAsync("{\"type\":\"login\",\"user\":{\"name\":\"bob\",\"id\":1}}"));
            Assert.Equal("a", await client.ExchangeAsync("LIST"));
            Assert.Equal("b", await client.ExchangeAsync("LIST"));
            Assert.Null(await client.ExchangeAsync("LIST"));
            server.Should().HaveReceived("PING", Times.Once());
        }

        [Fact]
        public async Task Tls_Configuration_Without_A_Certificate_Should_Use_A_Generated_One()
        {
            //Arrange: the generated certificate is not reachable from the server, so the client accepts any and checks its subject
            using var server = MockServer.FromJson("""
                { "version": 1, "server": { "transport": "tls", "framing": { "type": "delimiter", "delimiter": "\n" } },
                  "rules": [ { "request": "PING", "reply": "PONG" } ] }
                """);
            server.Start();
            string subject = null;

            //Act
            using var client = await LineClient.ConnectAsync(server.Port, certificate =>
            {
                subject = certificate.Subject;
                return true;
            });

            //Assert
            Assert.Equal("PONG", await client.ExchangeAsync("PING"));
            Assert.Equal("CN=localhost", subject);
        }

        [Fact]
        public async Task FromFile_Should_Load_A_Certificate_Relative_To_The_File()
        {
            //Arrange
            var directory = Path.Combine(Path.GetTempPath(), "rony-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                // TestCertificate.CreateSelfSigned() returns a certificate whose key cannot be exported on macOS, so build one here.
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                File.WriteAllBytes(Path.Combine(directory, "server.pfx"), certificate.Export(X509ContentType.Pfx, "secret"));
                var file = Path.Combine(directory, "mock.json");
                File.WriteAllText(file, """
                    { "version": 1,
                      "server": { "transport": "tls", "framing": { "type": "delimiter", "delimiter": "\n" },
                                  "tls": { "certificate": "server.pfx", "password": "secret", "protocol": "tls12" } },
                      "rules": [ { "request": "PING", "reply": "PONG" } ] }
                    """);
                using var server = MockServer.FromFile(file);
                server.Start();

                //Act
                using var client = await LineClient.ConnectAsync(server.Port, presented => presented.GetCertHashString() == certificate.GetCertHashString());

                //Assert
                Assert.Equal("PONG", await client.ExchangeAsync("PING"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void ValidateJson_Should_Not_Bind_The_Port_Of_A_Udp_Configuration()
        {
            using var busy = new UdpServer(new IPEndPoint(IPAddress.Loopback, 0));
            using var server = new MockServer(busy);
            server.Start();

            MockServer.ValidateJson("{ \"version\": 1, \"server\": { \"transport\": \"udp\", \"port\": " + busy.Port + " } }");
        }

        [Fact]
        public async Task Udp_Configuration_Should_Answer_Datagrams()
        {
            //Arrange
            using var server = MockServer.FromJson("""
                { "version": 1, "server": { "transport": "udp" }, "rules": [ { "request": "PING", "reply": "PONG" } ] }
                """);
            server.Start();

            //Act
            using var client = new UdpClient(AddressFamily.InterNetwork);
            var data = Encoding.UTF8.GetBytes("PING");
            await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Loopback, server.Port));
            var response = await client.ReceiveAsync().WaitAsync(Timeout);

            //Assert
            Assert.Equal("PONG", Encoding.UTF8.GetString(response.Buffer));
        }

        [Fact]
        public async Task Unix_Configuration_Should_Answer_On_The_Socket_File()
        {
            if (!Socket.OSSupportsUnixDomainSockets) return; // no dynamic skip in xUnit v2

            //Arrange
            var path = Path.Combine(Path.GetTempPath(), "rony-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock");
            using var server = MockServer.FromJson("""
                { "version": 1,
                  "server": { "transport": "unix", "path": "%PATH%", "framing": { "type": "delimiter", "delimiter": "\n" } },
                  "rules": [ { "request": "PING", "reply": "PONG" } ] }
                """.Replace("%PATH%", path.Replace("\\", "\\\\")));
            server.Start();

            //Act
            using var client = await LineClient.ConnectUnixAsync(path);

            //Assert
            Assert.Equal("PONG", await client.ExchangeAsync("PING"));
        }
    }
}
