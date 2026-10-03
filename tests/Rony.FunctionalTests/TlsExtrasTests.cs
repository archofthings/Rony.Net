using Rony.Listeners;
using Rony.Net;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Rony.FunctionalTests
{
    public class TlsExtrasTests
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task Mutual_Tls_Should_Accept_A_Client_Certificate_And_Expose_The_Tls_Details()
        {
            //Arrange
            using var clientCertificate = TestCertificate.CreateSelfSigned("my-client");
            var lines = new ConcurrentQueue<string>();
            X509Certificate2 validated = null;
            using var server = new MockServer(new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.Tls12)
            {
                RequireClientCertificate = true,
                ClientCertificateValidator = certificate => (validated = certificate) != null
            });
            server.Log = lines.Enqueue;
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            using var client = await ConnectAsync(server);
            await using var stream = await AuthenticateAsync(client, clientCertificate);
            var response = await SendAndReadAsync(stream, "ping");
            var connection = await server.WaitForConnectionAsync();

            //Assert
            Assert.Equal("pong", response);
            connection.Should().HaveUsedTls(SslProtocols.Tls12)
                .And.HaveServerName("LOCALHOST")
                .And.HavePresentedClientCertificate()
                .And.HavePresentedClientCertificate(clientCertificate);
            Assert.Equal(clientCertificate.GetCertHashString(), connection.Tls.ClientCertificate.GetCertHashString());
            Assert.Equal("localhost", connection.Tls.ServerName);
            Assert.Equal("CN=my-client", validated.Subject); // still readable: the validator's certificate is not disposed
            Assert.Contains(lines, line => line.Contains("connected from") && line.Contains("(Tls12, server name localhost, client certificate CN=my-client)"));
            Assert.Throws<MockVerificationException>(() => connection.Should().HaveUsedTls(SslProtocols.Tls13));
            Assert.Throws<MockVerificationException>(() => connection.Should().HaveServerName("other"));
            using var other = TestCertificate.CreateSelfSigned("other");
            Assert.Throws<MockVerificationException>(() => connection.Should().HavePresentedClientCertificate(other));
        }

        [Fact]
        public async Task Log_Lines_Should_Escape_Control_Characters_In_A_Client_Certificate_Subject()
        {
            //Arrange
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var certificateRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                new X500DistinguishedName("CN=\"evil\nfake log line\u001b[31m\""), rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var selfSigned = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            using var clientCertificate = new X509Certificate2(selfSigned.Export(X509ContentType.Pfx));
            var lines = new ConcurrentQueue<string>();
            using var server = new MockServer(new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.Tls12) { RequireClientCertificate = true });
            server.Log = lines.Enqueue;
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            using var client = await ConnectAsync(server);
            await using var stream = await AuthenticateAsync(client, clientCertificate);
            await SendAndReadAsync(stream, "ping");
            await server.WaitForConnectionAsync();

            //Assert
            Assert.Contains("\n", server.Connections[0].Tls.ClientCertificate.Subject);
            var line = Assert.Single(lines, l => l.Contains("connected from"));
            Assert.DoesNotContain('\n', line);
            Assert.DoesNotContain('\u001b', line);
            Assert.Contains("\\x0A", line);
        }

        [Theory]
        [InlineData("no certificate", "sent no certificate")]
        [InlineData("validator false", "rejected by ClientCertificateValidator")]
        [InlineData("validator throws", "ClientCertificateValidator threw")]
        public async Task Mutual_Tls_Should_Fail_The_Handshake_For_A_Rejected_Client_And_Keep_Working(string scenario, string reason)
        {
            //Arrange
            using var clientCertificate = TestCertificate.CreateSelfSigned("my-client");
            var listener = new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.Tls12) { RequireClientCertificate = true };
            listener.ClientCertificateValidator = scenario switch
            {
                "validator false" => _ => false,
                "validator throws" => _ => throw new InvalidOperationException("boom"),
                _ => null
            };
            var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.ConnectionFailed += (_, exception) => failed.TrySetResult(exception);
            var lines = new ConcurrentQueue<string>();
            using var server = new MockServer(listener);
            server.Log = lines.Enqueue;
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            using (var rejected = await ConnectAsync(server))
                Assert.True(await IsRejectedAsync(rejected, scenario == "no certificate" ? null : clientCertificate));
            var exception = await failed.Task.WaitAsync(ReadTimeout);
            listener.ClientCertificateValidator = null;
            using var good = await ConnectAsync(server);
            await using var goodStream = await AuthenticateAsync(good, clientCertificate);

            //Assert (only the good client is a connection)
            Assert.Contains(reason, exception.Message);
            Assert.Contains(lines, line => line.Contains("failed") && line.Contains(reason));
            Assert.Equal("pong", await SendAndReadAsync(goodStream, "ping"));
            Assert.Single(server.Connections);
        }

        [Fact]
        public async Task Without_RequireClientCertificate_No_Certificate_Is_Requested()
        {
            //Arrange
            using var clientCertificate = TestCertificate.CreateSelfSigned("my-client");
            using var server = new MockServer(new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.Tls12));
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            using var client = await ConnectAsync(server);
            await using var stream = await AuthenticateAsync(client, clientCertificate);
            await SendAndReadAsync(stream, "ping");
            var connection = await server.WaitForConnectionAsync();

            //Assert
            Assert.Null(connection.Tls.ClientCertificate);
            connection.Should().HaveUsedTls(SslProtocols.Tls12);
            Assert.Throws<MockVerificationException>(() => connection.Should().HavePresentedClientCertificate());
        }

        [Fact]
        public async Task A_Client_Connecting_To_An_Ip_Address_Sends_No_Server_Name()
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.Tls12));
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            using var client = await ConnectAsync(server);
            await using var stream = await AuthenticateAsync(client, null, "127.0.0.1");
            await SendAndReadAsync(stream, "ping");
            var connection = await server.WaitForConnectionAsync();

            //Assert
            Assert.Null(connection.Tls.ServerName);
            Assert.Throws<MockVerificationException>(() => connection.Should().HaveServerName("localhost"));
        }

        [Fact]
        public async Task A_Connection_Without_Tls_Should_Have_No_Tls_Details()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            using var client = await ConnectAsync(server);
            var connection = await server.WaitForConnectionAsync();

            //Assert
            Assert.Null(connection.Tls);
            Assert.Throws<MockVerificationException>(() => connection.Should().HaveUsedTls(SslProtocols.Tls12));
            Assert.Throws<MockVerificationException>(() => connection.Should().HaveServerName("localhost"));
            Assert.Throws<MockVerificationException>(() => connection.Should().HavePresentedClientCertificate());
        }

        private static async Task<TcpClient> ConnectAsync(MockServer server)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            return client;
        }

        /// <summary>
        /// Whether the client sees the rejection: the handshake fails, or (the client may finish its side first) the first
        /// read fails or ends. Needs no delay: the server closes the connection when it rejects the client.
        /// </summary>
        private static async Task<bool> IsRejectedAsync(TcpClient client, X509Certificate2 clientCertificate)
        {
            try
            {
                await using var stream = await AuthenticateAsync(client, clientCertificate);
                return await SendAndReadAsync(stream, "ping") == string.Empty;
            }
            catch (Exception exception) when (exception is AuthenticationException || exception is IOException)
            {
                return true;
            }
        }

        private static async Task<SslStream> AuthenticateAsync(TcpClient client, X509Certificate2 clientCertificate, string host = SharedCertificate.SubjectName)
        {
            var stream = new SslStream(client.GetStream(), false,
                (_, certificate, _, _) => certificate?.GetCertHashString() == SharedCertificate.Instance.GetCertHashString());
            try
            {
                var certificates = clientCertificate == null ? null : new X509CertificateCollection { clientCertificate };
                await stream.AuthenticateAsClientAsync(host, certificates, SslProtocols.Tls12, false);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static async Task<string> SendAndReadAsync(Stream stream, string request)
        {
            var data = request.GetBytes();
            await stream.WriteAsync(data, 0, data.Length);
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(ReadTimeout);
            var read = await stream.ReadAsync(buffer, timeout.Token);
            return buffer.Take(read).ToArray().GetString();
        }
    }
}
