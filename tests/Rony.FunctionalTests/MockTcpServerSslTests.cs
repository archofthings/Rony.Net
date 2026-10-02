using Rony.Listeners;
using Rony.Net;
using System;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Rony.FunctionalTests
{
    public class MockTcpServerSslTests
    {
        [Fact]
        public async Task Server_Should_Return_Correct_Response()
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
            var request = new byte[] { 1, 2, 3 };
            using var client = new TcpClient();

            //Act
            server.Mock.Send(request).Receive(x => new byte[] { x[1], 10, x[2] });
            server.Start();
            await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port);
            await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
            await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
            await sslStream.WriteAsync(request, 0, request.Length);
            var response = new byte[client.ReceiveBufferSize];
            var bytes = await sslStream.ReadAsync(response, 0, response.Length);
            client.Close();
            server.Stop();

            //Assert
            Assert.True(response.Take(bytes).SequenceEqual(new byte[] { 2, 10, 3 }));
        }

        [Theory]
        [InlineData("Match me")]
        [InlineData("12345")]
        [InlineData("****@#")]
        [InlineData("Match me too")]
        public async Task Server_Should_Return_Response_To_Any_Request_When_An_Empty_Request_Exists(string request)
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
            using var client = new TcpClient();

            //Act
            server.Mock.Send("").Receive("I match everything");
            server.Start();
            await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port);
            await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
            await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
            var requestBytes = request.GetBytes();
            await sslStream.WriteAsync(requestBytes, 0, requestBytes.Length);
            var response = new byte[client.ReceiveBufferSize];
            var bytes = await sslStream.ReadAsync(response, 0, response.Length);
            client.Close();
            server.Stop();

            //Assert
            Assert.Equal("I match everything", response.Take(bytes).ToArray().GetString());
        }

        [Theory]
        [InlineData("Match me")]
        [InlineData("12345")]
        [InlineData("****@#")]
        [InlineData("Match me too")]
        public async Task Server_Should_Return_Nothing_When_No_Match_Exists(string request)
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
            using var client = new TcpClient();

            //Act
            server.Mock.Send("Main Request").Receive(x => new byte[] { x[1], 10, x[2] });
            server.Start();
            await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port);
            await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
            await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
            var requestBytes = request.GetBytes();
            await sslStream.WriteAsync(requestBytes, 0, requestBytes.Length);
            var response = new byte[client.ReceiveBufferSize];
            var bytes = await sslStream.ReadAsync(response, 0, response.Length);
            client.Close();
            server.Stop();

            //Assert
            Assert.Equal(0, bytes);
        }

        [Fact]
        public async Task Server_Should_Return_Correct_Response_On_Multiple_Requests()
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
            using var client = new TcpClient();

            //Act
            server.Mock.Send("123").Receive("321");
            server.Mock.Send("ABC").Receive("CBA");
            server.Mock.Send("!@#").Receive("$%^");
            server.Start();
            await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port);
            await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
            await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
            await sslStream.WriteAsync("ABC".GetBytes(), 0, 3);
            var response = new byte[client.ReceiveBufferSize];
            var bytes = sslStream.Read(response, 0, response.Length);
            client.Close();
            server.Stop();

            //Assert
            Assert.Equal("CBA", response.Take(bytes).ToArray().GetString());
        }

        [Fact]
        public async Task Server_Should_Return_Correct_Response_On_Many_Request()
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));

            //Act
            for (int i = 0; i < 10000; i++)
                server.Mock.Send(i.ToString()).Receive((i + 10000).ToString());
            server.Start();
            // Every TLS handshake is expensive, so sample every 100th configured request.
            for (int i = 0; i < 10000; i += 100)
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port);
                await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
                await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
                var response = new byte[client.ReceiveBufferSize];
                var request = i.ToString().GetBytes();
                await sslStream.WriteAsync(request, 0, request.Length);
                var bytes = sslStream.Read(response, 0, response.Length);
                client.Close();

                //Assert
                Assert.Equal((i + 10000).ToString(), response.Take(bytes).ToArray().GetString());
            }
            server.Stop();
        }

        [Fact]
        public async Task Server_Should_Return_Correct_Response_On_Multi_Thread_Request()
        {
            //Act
            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                var index = i;
                tasks.Add(Task.Run(() => ConnectServer(index)));
            }

            //Assert
            await Task.WhenAll(tasks);

            async Task ConnectServer(int index)
            {
                var header = index.ToString();
                using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
                for (int i = 0; i < 20; i++)
                    server.Mock.Send($"{header}-{i}").Receive($"{header}-{i + 10000}");
                server.Start();
                for (int i = 0; i < 20; i++)
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, server.Port);
                    await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
                    await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
                    var buffer = new byte[client.ReceiveBufferSize];
                    var request = $"{header}-{i}".GetBytes();
                    await sslStream.WriteAsync(request, 0, request.Length);
                    var bytes = await sslStream.ReadAsync(buffer, 0, buffer.Length);
                    var response = buffer.Take(bytes).ToArray().GetString();

                    //Assert
                    Assert.Equal($"{header}-{i + 10000}", response);
                }
                server.Stop();
            }
        }

        [Fact]
        public async Task Server_Should_Keep_Running_After_A_Failed_Handshake()
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
            server.Mock.Send("Request").Receive("Response");
            server.Start();

            //Act
            using (var badClient = new TcpClient())
            {
                // Plain text instead of a TLS handshake
                await badClient.ConnectAsync(IPAddress.Loopback, server.Port);
                var garbage = "not a tls handshake".GetBytes();
                await badClient.GetStream().WriteAsync(garbage, 0, garbage.Length);
            }

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
            await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
            var request = "Request".GetBytes();
            await sslStream.WriteAsync(request, 0, request.Length);
            var response = new byte[client.ReceiveBufferSize];
            var bytes = await sslStream.ReadAsync(response, 0, response.Length);
            server.Stop();

            //Assert
            Assert.Equal("Response", response.Take(bytes).ToArray().GetString());
        }

        private static bool CertificateValidationCallback(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // The test certificate is self-signed, so trust exactly that certificate and nothing else.
            return certificate != null && certificate.GetCertHashString() == TestCertificate.Instance.GetCertHashString();
        }

        [Theory]
        [InlineData("Match Me", "MtM")]
        [InlineData("0123456789", "026")]
        [InlineData("Try Me too", "Ty ")]
        [InlineData("@762Rt%", "@6%")]
        public async Task Server_Should_Return_Correct_Response_Where_Configed_With_Enything_And_Func_Of_Byte(string request, string expected)
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
            using var client = new TcpClient();

            //Act
            server.Mock.Send("").Receive(x => new byte[] { x[0], x[2], x[6] });
            server.Start();
            await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port);
            await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
            await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
            await sslStream.WriteAsync(request.GetBytes(), 0, request.Length);
            var response = new byte[client.ReceiveBufferSize];
            var bytes = await sslStream.ReadAsync(response, 0, response.Length);
            client.Close();
            server.Stop();

            //Assert
            Assert.Equal(expected, response.Take(bytes).ToArray().GetString());
        }

        [Theory]
        [InlineData("Match Me", "MATC")]
        [InlineData("0123456789", "0123")]
        [InlineData("Try Me too", "TRY ")]
        [InlineData("@762Rt%", "@762")]
        public async Task Server_Should_Return_Correct_Response_Where_Configed_With_Enything_And_Func_Of_String(string request, string expected)
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
            using var client = new TcpClient();

            //Act
            server.Mock.Send("").Receive(x => x.Substring(0,4).ToUpper());
            server.Start();
            await client.ConnectAsync(IPAddress.Parse("127.0.0.1"), server.Port);
            await using var sslStream = new SslStream(client.GetStream(), false, CertificateValidationCallback);
            await sslStream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
            await sslStream.WriteAsync(request.GetBytes(), 0, request.Length);
            var response = new byte[client.ReceiveBufferSize];
            var bytes = await sslStream.ReadAsync(response, 0, response.Length);
            client.Close();
            server.Stop();

            //Assert
            Assert.Equal(expected, response.Take(bytes).ToArray().GetString());
        }
    }
}