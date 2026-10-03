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
            using var server = new MockServer(new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.None));
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
            // The delay is the point of this test: the framed message must arrive in two separate reads.
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
        public async Task StxEtx_Framing_Should_Split_Messages_And_Frame_Responses()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.StxEtx });
            server.Mock.Send("one").Receive("1");
            server.Mock.Send("two").Receive("2");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await stream.WriteAsync(new byte[] { 0x02, (byte)'o', (byte)'n', (byte)'e', 0x03, 0x02, (byte)'t' });
            await stream.WriteAsync(new byte[] { (byte)'w', (byte)'o', 0x03 });
            var responses = await ReadExactlyAsync(stream, 6);

            //Assert
            Assert.Equal(new byte[] { 0x02, (byte)'1', 0x03, 0x02, (byte)'2', 0x03 }, responses);
        }

        [Fact]
        public async Task LengthPrefix_Including_The_Prefix_Should_Close_Only_The_Connection_With_An_Invalid_Length()
        {
            //Arrange
            var tcpServer = new TcpServer(0) { Framing = MessageFraming.LengthPrefix(2, true, true) };
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var server = new MockServer(tcpServer) { Log = lines.Enqueue };
            server.Mock.Send(new byte[] { 0x01 }).Receive(new byte[] { 0x02 });
            server.Start();
            var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcpServer.ConnectionFailed += (_, exception) => failed.TrySetResult(exception);
            using var bad = await ConnectAsync(server);
            using var good = await ConnectAsync(server);
            var goodStream = good.GetStream();

            //Act
            await bad.GetStream().WriteAsync(new byte[] { 0x00, 0x01, 0x09 });
            var exception = await failed.Task.WaitAsync(ReadTimeout);
            var badResponse = await ReadToEndAsync(bad.GetStream());
            await goodStream.WriteAsync(new byte[] { 0x00, 0x03, 0x01 });
            var response = await ReadExactlyAsync(goodStream, 3);

            //Assert
            Assert.IsType<InvalidDataException>(exception);
            Assert.Equal("", badResponse);
            Assert.Equal(new byte[] { 0x00, 0x03, 0x02 }, response);
            Assert.Contains(lines, line => line.Contains("failed"));
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

        [Fact]
        public async Task StartAsync_Should_Listen_And_DisposeAsync_Should_Stop()
        {
            //Arrange
            MockServer server;
            await using (server = new MockServer(new TcpServer(0)))
            {
                server.Mock.Send("ping").Receive("pong");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();

                //Act
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.StartAsync(cancelled.Token));
                Assert.False(server.Active);
                await server.StartAsync();
                using var client = await ConnectAsync(server);
                var response = await SendAndReadAsync(client.GetStream(), "ping");

                //Assert
                Assert.Equal("pong", response);
            }

            Assert.False(server.Active);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task StopAsync_Should_Wait_For_Work_In_Flight_And_Call_No_Callback_Afterwards(bool tls)
        {
            //Arrange
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var server = new MockServer(tls
                ? new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.None)
                : new TcpServer(0));
            server.Log = lines.Enqueue;
            server.Mock.Send("slow").Receive("done").After(TimeSpan.FromSeconds(30));
            await server.StartAsync();
            using var client = await ConnectAsync(server);
            Stream stream = client.GetStream();
            if (tls) stream = await AuthenticateAsync(client);
            await using var _ = stream;
            await WriteAsync(stream, "slow");
            await server.Mock.WaitForRequestAsync("slow");
            var stopwatch = Stopwatch.StartNew();

            //Act
            await server.StopAsync();
            var count = lines.Count;
            await server.WaitForAllConnectionsClosedAsync();

            //Assert
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"StopAsync took {stopwatch.Elapsed}");
            Assert.Equal(count, lines.Count);
        }

        [Fact]
        public async Task StopAsync_Should_Be_Repeatable_Work_When_Unstarted_And_Allow_Restart()
        {
            //Arrange
            await using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ping").Receive("pong");

            //Act
            await server.StopAsync();   // never started
            await server.StartAsync();
            var port = server.Port;
            await server.StopAsync();
            await server.StopAsync();
            Assert.False(server.Active);
            await server.StartAsync();
            using var client = await ConnectAsync(server);
            var response = await SendAndReadAsync(client.GetStream(), "ping");

            //Assert
            Assert.Equal(port, server.Port);
            Assert.Equal("pong", response);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task ResetConnection_Should_Reset_The_Client_Instead_Of_Closing_Cleanly(bool tls, bool viaConnection)
        {
            //Arrange
            using var server = new MockServer(tls
                ? new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.None)
                : new TcpServer(0));
            server.Mock.Send("X").ResetConnection();
            server.Start();
            using var client = await ConnectAsync(server);
            Stream stream = client.GetStream();
            if (tls) stream = await AuthenticateAsync(client);
            await using var _ = stream;
            var connection = await server.WaitForConnectionAsync();

            //Act
            if (viaConnection)
                await connection.ResetAsync();
            else
                await WriteAsync(stream, "X");
            using var timeout = new CancellationTokenSource(ReadTimeout);

            //Assert (a clean close would read 0 bytes instead of failing)
            await Assert.ThrowsAnyAsync<IOException>(async () => await stream.ReadAsync(new byte[16], timeout.Token));
            await connection.WaitForCloseAsync();
        }

        [Fact]
        public async Task Truncated_Should_Send_Only_The_First_Bytes_Of_The_Framed_Response()
        {
            //Arrange (length prefix 0000000B announces 11 bytes, only 2 follow)
            using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.LengthPrefix() });
            server.Mock.Send("X").Receive("HELLO WORLD").Truncated(6).AndDisconnect();
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await stream.WriteAsync(new byte[] { 0, 0, 0, 1, (byte)'X' });
            using var memory = new MemoryStream();
            using var timeout = new CancellationTokenSource(ReadTimeout);
            await stream.CopyToAsync(memory, timeout.Token);

            //Assert
            Assert.Equal(new byte[] { 0, 0, 0, 11, (byte)'H', (byte)'E' }, memory.ToArray());
        }

        [Fact]
        public async Task Truncated_Reply_Should_Keep_The_Connection_Usable()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("X").Receive("HELLO").Truncated(2);
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            var first = await SendAndReadAsync(stream, "X");
            var second = await SendAndReadAsync(stream, "X");

            //Assert
            Assert.Equal("HE", first);
            Assert.Equal("HE", second);
        }

        [Fact]
        public async Task AndResetConnection_Should_Reset_The_Client_After_The_Reply()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("X").Receive("partial").AndResetConnection();
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();
            var connection = await server.WaitForConnectionAsync();

            //Act
            await WriteAsync(stream, "X");
            using var timeout = new CancellationTokenSource(ReadTimeout);

            //Assert (the reply may be discarded by the reset; a clean close would end the read with 0 bytes instead of failing)
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                var buffer = new byte[16];
                while (await stream.ReadAsync(buffer, timeout.Token) > 0)
                {
                }
            });
            await connection.WaitForCloseAsync();
            server.Should().HaveNoOpenConnections();
        }

        [Fact]
        public async Task Truncated_Greeting_Should_Send_Only_The_First_Bytes_And_Keep_The_Connection_Usable()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.OnConnect().Receive("hello").Truncated(2);
            server.Mock.Send("ping").Receive("pong");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            var greeting = await ReadExactlyAsync(stream, 2);
            var response = await SendAndReadAsync(stream, "ping");

            //Assert
            Assert.Equal("he", greeting.GetString());
            Assert.Equal("pong", response);
        }

        [Fact]
        public async Task Corrupted_Should_Change_A_Copy_Of_The_Response()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("X").Receive("HELLO").Corrupted(bytes => { bytes[0] ^= 0xFF; return bytes; });
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await WriteAsync(stream, "X");
            var first = await ReadExactlyAsync(stream, 5);
            await WriteAsync(stream, "X");
            var second = await ReadExactlyAsync(stream, 5);

            //Assert (the configured response is untouched, so both replies are corrupted the same way)
            Assert.Equal(new byte[] { (byte)'H' ^ 0xFF, (byte)'E', (byte)'L', (byte)'L', (byte)'O' }, first);
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task Corrupted_Function_That_Throws_Should_Send_The_Unmodified_Response()
        {
            //Arrange
            var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var server = new MockServer(new TcpServer(0)) { Log = log.Enqueue };
            server.Mock.Send("X").Receive("HELLO").Corrupted(_ => throw new InvalidOperationException("boom"));
            server.Start();
            using var client = await ConnectAsync(server);

            //Act
            var response = await SendAndReadAsync(client.GetStream(), "X");

            //Assert
            Assert.Equal("HELLO", response);
            Assert.Contains(log, line => line.Contains("error") && line.Contains("boom"));
        }

        [Fact]
        public async Task InChunks_Should_Send_The_Framed_Response_In_Pieces()
        {
            //Arrange (length prefix 0000000B + 11 bytes = 15 bytes, in pieces of 4)
            using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.LengthPrefix() });
            server.Mock.Send("X").Receive("HELLO WORLD").InChunks(4, TimeSpan.FromMilliseconds(300));
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            await stream.WriteAsync(new byte[] { 0, 0, 0, 1, (byte)'X' });
            var first = new byte[15];
            using var timeout = new CancellationTokenSource(ReadTimeout);
            var firstRead = await stream.ReadAsync(first, timeout.Token);
            var rest = await ReadExactlyAsync(stream, 15 - firstRead);

            //Assert (the first read can only see the first piece: the second is sent 300 ms later)
            Assert.Equal(4, firstRead);
            var all = first.Take(firstRead).Concat(rest).ToArray();
            Assert.Equal(new byte[] { 0, 0, 0, 11 }.Concat("HELLO WORLD".GetBytes()).ToArray(), all);
        }

        [Fact]
        public async Task Throttled_Should_Take_As_Long_As_The_Rate_Requires()
        {
            //Arrange (100 bytes/s = 10 bytes every 100 ms: 50 bytes need 4 waits)
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("X").Receive(new string('a', 50)).Throttled(bytesPerSecond: 100);
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act
            var watch = Stopwatch.StartNew();
            await WriteAsync(stream, "X");
            var response = await ReadExactlyAsync(stream, 50);
            watch.Stop();

            //Assert
            Assert.Equal(new string('a', 50), response.GetString());
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(300), $"took only {watch.Elapsed}");
        }

        [Fact]
        public async Task Push_During_A_Chunked_Response_Should_Wait_For_The_Last_Piece()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            var body = new string('a', 20);
            server.Mock.Send("X").Receive(body).InChunks(5, TimeSpan.FromMilliseconds(100));
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();
            var connection = await server.WaitForConnectionAsync();

            //Act
            await WriteAsync(stream, "X");
            var first = await ReadExactlyAsync(stream, 1);
            await connection.SendAsync("PUSH");
            var rest = await ReadExactlyAsync(stream, 23);

            //Assert
            Assert.Equal(body + "PUSH", first.Concat(rest).ToArray().GetString());
        }

        [Fact]
        public async Task StopAsync_Should_Complete_While_A_Slow_Response_Is_Being_Sent()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("X").Receive("slow response").InChunks(1, TimeSpan.FromMinutes(1));
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();
            await WriteAsync(stream, "X");
            await ReadExactlyAsync(stream, 1);

            //Act + Assert
            await server.StopAsync().WaitAsync(ReadTimeout);
        }

        [Fact]
        public async Task Udp_Should_Send_A_Chunked_Response_Whole()
        {
            //Arrange
            using var server = new MockServer(new UdpServer("127.0.0.1", 0));
            server.Mock.Send("ping").Receive("pong").InChunks(1, TimeSpan.FromMinutes(1));
            server.Start();
            using var client = new UdpClient();

            //Act
            var request = "ping".GetBytes();
            await client.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Loopback, server.Port));
            var response = await client.ReceiveAsync().WaitAsync(ReadTimeout);

            //Assert
            Assert.Equal("pong", response.Buffer.GetString());
        }

        [Fact]
        public async Task RefuseConnections_Should_Refuse_New_Clients_Until_AcceptConnections()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("ping").Receive("pong");
            server.Start();
            var port = server.Port;
            using var open = await ConnectAsync(server);
            var openStream = open.GetStream();
            await server.WaitForConnectionAsync();

            //Act
            server.RefuseConnections();
            server.RefuseConnections();
            using var refusedClient = new TcpClient();
            var refused = await Assert.ThrowsAsync<SocketException>(() => refusedClient.ConnectAsync(IPAddress.Loopback, port));
            var answeredWhileRefusing = await SendAndReadAsync(openStream, "ping");
            server.AcceptConnections();
            using var later = await ConnectAsync(server);
            var answeredAfterwards = await SendAndReadAsync(later.GetStream(), "ping");

            //Assert
            Assert.True(refused.SocketErrorCode == SocketError.ConnectionRefused, $"Expected ConnectionRefused but got {refused.SocketErrorCode}: {refused.Message}");
            Assert.Equal("pong", answeredWhileRefusing);
            Assert.Equal("pong", answeredAfterwards);
            Assert.True(server.Active);
            Assert.Equal(port, server.Port);
            await server.StopAsync().WaitAsync(ReadTimeout);
        }

        [Fact]
        public void RefuseConnections_Should_Throw_When_The_Server_Is_Not_Started()
        {
            using var server = new MockServer(new TcpServer(0));

            Assert.Throws<InvalidOperationException>(() => server.RefuseConnections());
        }

        [Fact]
        public async Task FailHandshake_Should_Fail_The_Client_Handshake_And_Keep_The_Connection_Out_Of_The_Server()
        {
            //Arrange
            var listener = new TcpServerSsl(0, SharedCertificate.Instance, SslProtocols.None) { FailHandshake = true };
            var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.ConnectionFailed += (_, _) => failed.TrySetResult(true);
            using var server = new MockServer(listener);
            server.Mock.Send("ping").Receive("pong");
            server.Start();

            //Act
            using (var client = await ConnectAsync(server))
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await using var stream = await AuthenticateAsync(client);
                });
            await failed.Task.WaitAsync(ReadTimeout);
            listener.FailHandshake = false;
            using var working = await ConnectAsync(server);
            await using var workingStream = await AuthenticateAsync(working);

            //Assert (AuthenticationException or IOException, depending on the platform; only the working client is a connection)
            Assert.Equal("pong", await SendAndReadAsync(workingStream, "ping"));
            Assert.Single(server.Connections);
        }

        [Fact]
        public async Task Json_And_Capture_Group_Rules_Should_Answer_Over_A_Socket()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.SendJson(j => j["type"].AsString() == "login").Receive("{\"ok\":true}");
            server.Mock.Send(new Regex(@"^HELLO (\w+)$")).ReceiveMatch(m => $"HI {m.Groups[1].Value}");
            server.Start();
            using var client = await ConnectAsync(server);
            var stream = client.GetStream();

            //Act + Assert
            Assert.Equal("{\"ok\":true}", await SendAndReadAsync(stream, "{\"type\":\"login\"}"));
            Assert.Equal("HI bob", await SendAndReadAsync(stream, "HELLO bob"));
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
                (sender, certificate, chain, errors) => certificate?.GetCertHashString() == SharedCertificate.Instance.GetCertHashString());
            await stream.AuthenticateAsClientAsync(SharedCertificate.SubjectName);
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
