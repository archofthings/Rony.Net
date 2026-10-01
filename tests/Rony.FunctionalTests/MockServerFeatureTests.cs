using Rony.Listeners;
using Rony.Net;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Rony.FunctionalTests
{
    /// <summary>
    /// Every server here uses port 0, so the OS picks a free port and tests never collide.
    /// </summary>
    public class MockServerFeatureTests
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task Tcp_Connection_Should_Stay_Open_For_Multiple_Requests()
        {
            //Arrange (scenario from issue #1)
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ping").Receive("pong");
            server.Mock.Send("pong").Receive("ping");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            var response1 = await SendAndReadAsync(stream, "ping");
            var response2 = await SendAndReadAsync(stream, "pong");
            var response3 = await SendAndReadAsync(stream, "ping");

            //Assert
            Assert.Equal("pong", response1);
            Assert.Equal("ping", response2);
            Assert.Equal("pong", response3);
        }

        [Fact]
        public async Task Ssl_Connection_Should_Stay_Open_For_Multiple_Requests()
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None));
            server.Mock.Send("ping").Receive("pong");
            server.Mock.Send("pong").Receive("ping");
            server.Start();
            using var client = await ConnectAsync(server);
            await using var stream = await AuthenticateAsync(client);

            //Act
            var response1 = await SendAndReadAsync(stream, "ping");
            var response2 = await SendAndReadAsync(stream, "pong");

            //Assert
            Assert.Equal("pong", response1);
            Assert.Equal("ping", response2);
        }

        [Fact]
        public async Task KeepAlive_False_Should_Close_Connection_After_Reply()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0) { KeepAlive = false });
            server.Mock.Send("ping").Receive("pong");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await WriteAsync(stream, "ping");
            var response = await ReadToEndAsync(stream);

            //Assert
            Assert.Equal("pong", response);
        }

        [Fact]
        public async Task Port_Zero_Should_Assign_A_Free_Port_And_Keep_It_On_Restart()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ping").Receive("pong");

            //Act
            server.Start();
            var port = server.Port;
            server.Stop();
            server.Start();
            using var client = await ConnectAsync(server);
            var response = await SendAndReadAsync(client.GetStream(), "ping");

            //Assert
            Assert.NotEqual(0, port);
            Assert.Equal(port, server.Port);
            Assert.Equal("pong", response);
        }

        [Fact]
        public async Task Udp_Port_Zero_Should_Assign_A_Free_Port()
        {
            //Arrange
            using var server = new MockServer(new UdpServer("127.0.0.1", 0));
            server.Mock.Send("ping").Receive("pong");
            server.Start();
            using var client = new UdpClient();

            //Act
            var request = "ping".GetBytes();
            await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, server.Port));
            var response = await client.ReceiveAsync().WaitAsync(ReadTimeout);

            //Assert
            Assert.NotEqual(0, server.Port);
            Assert.Equal("pong", response.Buffer.GetString());
        }

        [Fact]
        public async Task Delimiter_Framing_Should_Split_Messages_And_Frame_Responses()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
            server.Mock.Send("one").Receive("1");
            server.Mock.Send("two").Receive("2");
            server.Mock.Send("three").Receive("3");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await WriteAsync(stream, "one\ntwo\nthr");
            await Task.Delay(50);
            await WriteAsync(stream, "ee\n");
            var responses = await ReadExactlyAsync(stream, 6);

            //Assert
            Assert.Equal("1\n2\n3\n", responses.GetString());
            Assert.Equal(new[] { "one", "two", "three" }, server.ReceivedRequests.Select(r => r.BodyString));
        }

        [Fact]
        public async Task LengthPrefix_Framing_Should_Handle_Binary_Messages()
        {
            //Arrange
            var framing = MessageFraming.LengthPrefix(2);
            using var server = new MockServer(new TcpServer(0) { Framing = framing });
            server.Mock.Send(new byte[] { 0x01, 0xFF }).Receive(x => x.Reverse().ToArray());
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            var request = framing.Encode(new byte[] { 0x01, 0xFF });
            await stream.WriteAsync(request.Concat(request).ToArray());
            var responses = await ReadExactlyAsync(stream, 8);

            //Assert
            Assert.Equal(new byte[] { 0, 2, 0xFF, 0x01, 0, 2, 0xFF, 0x01 }, responses);
        }

        [Fact]
        public async Task Sequence_Should_Return_Responses_In_Order_On_One_Connection()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("status").Receive("busy").Then("busy").Then("ready");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            var responses = new[]
            {
                await SendAndReadAsync(stream, "status"),
                await SendAndReadAsync(stream, "status"),
                await SendAndReadAsync(stream, "status"),
                await SendAndReadAsync(stream, "status")
            };

            //Assert
            Assert.Equal(new[] { "busy", "busy", "ready", "ready" }, responses);
        }

        [Fact]
        public async Task Pipelined_Requests_Should_Each_Get_Exactly_One_Response_In_Order()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
            server.Mock.SendMatching(_ => true).Receive((string x) => x);
            server.Start();

            for (var round = 0; round < 50; round++)
            {
                using var client = await ConnectAsync(server);
                var stream = client.GetStream();
                var lines = Enumerable.Range(0, 20).Select(i => $"{round}-{i}").ToArray();

                //Act
                await WriteAsync(stream, string.Join("\n", lines) + "\n");
                var expected = string.Join("\n", lines) + "\n";
                var responses = await ReadExactlyAsync(stream, expected.Length);

                //Assert
                Assert.Equal(expected, responses.GetString());
            }
        }

        [Fact]
        public async Task After_Should_Delay_The_Response()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("slow").Receive("done").After(TimeSpan.FromMilliseconds(300));
            server.Start();
            using var client = await ConnectAsync(server);
            var stopwatch = Stopwatch.StartNew();

            //Act
            var response = await SendAndReadAsync(client.GetStream(), "slow");

            //Assert
            Assert.Equal("done", response);
            Assert.True(stopwatch.ElapsedMilliseconds >= 250, $"Response came after {stopwatch.ElapsedMilliseconds} ms");
        }

        [Fact]
        public async Task Delayed_Response_Should_Not_Be_Overtaken_On_The_Same_Connection()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
            server.Mock.Send("slow").Receive("first").After(TimeSpan.FromMilliseconds(300));
            server.Mock.Send("fast").Receive("second");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await WriteAsync(stream, "slow\nfast\n");
            var responses = await ReadExactlyAsync(stream, 13);

            //Assert
            Assert.Equal("first\nsecond\n", responses.GetString());
        }

        [Fact]
        public async Task Disconnect_Should_Close_Connection_Without_Reply()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("bye").Disconnect();
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await WriteAsync(stream, "bye");
            var response = await ReadToEndAsync(stream);

            //Assert
            Assert.Equal("", response);
        }

        [Fact]
        public async Task AndDisconnect_Should_Reply_Then_Close()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("quit").Receive("goodbye").AndDisconnect();
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await WriteAsync(stream, "quit");
            var response = await ReadToEndAsync(stream);

            //Assert
            Assert.Equal("goodbye", response);
        }

        [Fact]
        public async Task NoReply_Should_Stay_Silent_And_Keep_The_Connection_Usable()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ignored").NoReply();
            server.Mock.Send("ping").Receive("pong");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await WriteAsync(stream, "ignored");
            using var shortTimeout = new CancellationTokenSource(300);
            var buffer = new byte[16];

            //Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.ReadAsync(buffer, shortTimeout.Token));
            await server.Mock.WaitForRequestAsync("ignored");
            Assert.Equal("pong", await SendAndReadAsync(stream, "ping"));
        }

        [Fact]
        public async Task Silent_Client_Should_Not_Block_Other_Clients()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            using var silentClient = await ConnectAsync(server);
            using var client = await ConnectAsync(server);
            var response = await SendAndReadAsync(client.GetStream(), "ping");

            //Assert
            Assert.Equal("pong", response);
        }

        [Fact]
        public async Task Client_That_Stops_Sending_Should_Still_Get_Its_Reply()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ping").Receive("pong").After(TimeSpan.FromMilliseconds(100));
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await WriteAsync(stream, "ping");
            client.Client.Shutdown(SocketShutdown.Send);
            var response = await ReadToEndAsync(stream);

            //Assert
            Assert.Equal("pong", response);
        }

        [Fact]
        public async Task Server_Should_Record_And_Verify_Requests()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send(new Regex("^PING")).Receive("PONG");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await SendAndReadAsync(stream, "PING 1");
            await SendAndReadAsync(stream, "PING 2");
            await SendAndReadAsync(stream, "HELLO");
            var request = await server.Mock.WaitForRequestAsync("HELLO");

            //Assert
            server.Mock.Verify(r => r.BodyString.StartsWith("PING"), Times.Exactly(2));
            Assert.False(request.Matched);
            Assert.Equal(((IPEndPoint)client.Client.LocalEndPoint).Port, ((IPEndPoint)request.RemoteEndPoint).Port);
            Assert.Throws<MockVerificationException>(() => server.Mock.VerifyAllRequestsMatched());
        }

        [Fact]
        public async Task Udp_Should_Record_Remote_EndPoint()
        {
            //Arrange
            using var server = new MockServer(new UdpServer("127.0.0.1", 0));
            server.Mock.Send("").NoReply();
            server.Start();
            using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            //Act
            var request = "hello".GetBytes();
            await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, server.Port));
            var received = await server.Mock.WaitForRequestAsync("hello");

            //Assert
            Assert.Equal(((IPEndPoint)client.Client.LocalEndPoint).Port, ((IPEndPoint)received.RemoteEndPoint).Port);
        }

        [Fact]
        public async Task Stop_Should_Close_Open_Connections()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ping").Receive("pong");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();
            await SendAndReadAsync(stream, "ping");

            //Act
            server.Stop();
            var rest = await ReadToEndAsync(stream);

            //Assert
            Assert.Equal("", rest);
        }

        private static async Task<TcpClient> ConnectAsync(MockServer server)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            return client;
        }

        private static async Task<SslStream> AuthenticateAsync(TcpClient client)
        {
            var stream = new SslStream(client.GetStream(), false,
                (sender, certificate, chain, errors) => certificate?.GetCertHashString() == TestCertificate.Instance.GetCertHashString());
            await stream.AuthenticateAsClientAsync(TestCertificate.SubjectName);
            return stream;
        }

        private static Task WriteAsync(Stream stream, string text)
        {
            var data = text.GetBytes();
            return stream.WriteAsync(data, 0, data.Length);
        }

        private static async Task<string> SendAndReadAsync(Stream stream, string request)
        {
            await WriteAsync(stream, request);
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(ReadTimeout);
            var read = await stream.ReadAsync(buffer, timeout.Token);
            return buffer.Take(read).ToArray().GetString();
        }

        private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            using var timeout = new CancellationTokenSource(ReadTimeout);
            await stream.ReadExactlyAsync(buffer, timeout.Token);
            return buffer;
        }

        private static async Task<string> ReadToEndAsync(Stream stream)
        {
            using var timeout = new CancellationTokenSource(ReadTimeout);
            using var memory = new MemoryStream();
            try
            {
                await stream.CopyToAsync(memory, timeout.Token);
            }
            catch (IOException)
            {
                // Connection reset counts as closed.
            }
            return memory.ToArray().GetString();
        }
    }
}
