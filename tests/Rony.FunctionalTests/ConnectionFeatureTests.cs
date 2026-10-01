using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Rony.FunctionalTests
{
    /// <summary>
    /// Connections, greetings, pushed messages, scenario state over real sockets, unmatched requests and logging.
    /// Servers use line framing, so messages that arrive together are still read one by one.
    /// </summary>
    public class ConnectionFeatureTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        private static MockServer LineServer(Action<MockServer> configure = null)
        {
            var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
            configure?.Invoke(server);
            server.Start();
            return server;
        }

        [Fact]
        public async Task Greeting_Should_Arrive_Before_Any_Response()
        {
            //Arrange
            using var server = LineServer(s =>
            {
                s.Mock.OnConnect().Receive("220 mock ready");
                s.Mock.Send("HELO test").Receive("250 hello");
            });

            //Act
            using var client = await LineClient.ConnectAsync(server.Port);
            await client.SendAsync("HELO test");

            //Assert
            Assert.Equal("220 mock ready", await client.ReadLineAsync());
            Assert.Equal("250 hello", await client.ReadLineAsync());
        }

        [Fact]
        public async Task Greeting_Sequence_Should_Differ_Per_Connection()
        {
            //Arrange
            using var server = LineServer(s => s.Mock.OnConnect().Receive("220 ready").Then("421 busy").AndDisconnect());

            //Act
            using var first = await LineClient.ConnectAsync(server.Port);
            var firstGreeting = await first.ReadLineAsync();
            using var second = await LineClient.ConnectAsync(server.Port);
            var secondGreeting = await second.ReadLineAsync();
            var afterSecond = await second.ReadLineAsync();

            //Assert
            Assert.Equal("220 ready", firstGreeting);
            Assert.Equal("421 busy", secondGreeting);
            Assert.Null(afterSecond);
        }

        [Fact]
        public async Task OnConnect_Disconnect_Should_Refuse_Connections()
        {
            //Arrange
            using var server = LineServer(s => s.Mock.OnConnect().Disconnect());

            //Act
            using var client = await LineClient.ConnectAsync(server.Port);

            //Assert
            Assert.Null(await client.ReadLineAsync());
            await server.Connections.Single().WaitForCloseAsync();
        }

        [Fact]
        public async Task Connections_Should_Be_Tracked_With_Their_Requests()
        {
            //Arrange
            using var server = LineServer(s => s.Mock.Send("").Receive(text => text.ToUpperInvariant()));
            var opened = new ConcurrentQueue<ClientConnection>();
            server.ConnectionOpened += (_, connection) => opened.Enqueue(connection);

            //Act
            using var first = await LineClient.ConnectAsync(server.Port);
            await first.SendAsync("a");
            await first.ReadLineAsync();
            using var second = await LineClient.ConnectAsync(server.Port);
            await second.SendAsync("b");
            await second.ReadLineAsync();
            await first.SendAsync("c");
            await first.ReadLineAsync();

            //Assert
            var connections = await server.WaitForConnectionsAsync(2);
            Assert.Equal(new[] { 1, 2 }, connections.Select(c => c.Id));
            Assert.Equal(new[] { "a", "c" }, connections[0].ReceivedRequests.Select(r => r.BodyString));
            Assert.Equal(new[] { "b" }, connections[1].ReceivedRequests.Select(r => r.BodyString));
            Assert.All(server.ReceivedRequests, r => Assert.NotNull(r.ConnectionId));
            Assert.Equal(((IPEndPoint)first.LocalEndPoint).Port, ((IPEndPoint)connections[0].RemoteEndPoint).Port);
            Assert.Equal(2, opened.Count);
            Assert.Equal(2, server.OpenConnections.Count);
            server.VerifyConnections(Times.Exactly(2));
            Assert.Throws<MockVerificationException>(() => server.VerifyConnections(Times.Once()));
        }

        [Fact]
        public async Task Closing_The_Client_Should_Close_The_Connection()
        {
            //Arrange
            using var server = LineServer(s => s.Mock.Send("ping").Receive("pong"));
            var closed = new TaskCompletionSource<ClientConnection>();
            server.ConnectionClosed += (_, connection) => closed.TrySetResult(connection);
            var client = await LineClient.ConnectAsync(server.Port);
            await client.SendAsync("ping");
            await client.ReadLineAsync();
            var connection = await server.WaitForConnectionAsync();

            //Act
            client.Dispose();

            //Assert
            await connection.WaitForCloseAsync();
            Assert.False(connection.IsOpen);
            Assert.NotNull(connection.ClosedAt);
            Assert.Same(connection, await closed.Task.WaitAsync(Timeout));
            Assert.Empty(server.OpenConnections);
        }

        [Fact]
        public async Task Closing_The_Client_After_NoReply_Should_Close_The_Connection()
        {
            //Arrange
            using var server = LineServer(s => s.Mock.Send("ping").NoReply());
            var client = await LineClient.ConnectAsync(server.Port);
            await client.SendAsync("ping");
            await server.Mock.WaitForRequestAsync("ping");
            var connection = server.Connections.Single();

            //Act
            client.Dispose();

            //Assert
            await connection.WaitForCloseAsync();
        }

        [Fact]
        public async Task WaitForCloseAsync_Should_Time_Out_While_Open()
        {
            //Arrange
            using var server = LineServer();
            using var client = await LineClient.ConnectAsync(server.Port);
            var connection = await server.WaitForConnectionAsync();

            //Act & Assert
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => connection.WaitForCloseAsync(TimeSpan.FromMilliseconds(100)));
            Assert.Contains("#1", exception.Message);
        }

        [Fact]
        public async Task Server_Should_Push_To_One_Connection_Or_Broadcast()
        {
            //Arrange
            using var server = LineServer();
            using var first = await LineClient.ConnectAsync(server.Port);
            using var second = await LineClient.ConnectAsync(server.Port);
            var connections = await server.WaitForConnectionsAsync(2);

            //Act
            await connections[1].SendAsync("only you");
            var sent = await server.BroadcastAsync("everyone");

            //Assert
            Assert.Equal(2, sent);
            Assert.Equal("only you", await second.ReadLineAsync());
            Assert.Equal("everyone", await second.ReadLineAsync());
            Assert.Equal("everyone", await first.ReadLineAsync());
        }

        [Fact]
        public async Task Server_Should_Close_A_Connection_On_Request()
        {
            //Arrange
            using var server = LineServer();
            using var client = await LineClient.ConnectAsync(server.Port);
            var connection = await server.WaitForConnectionAsync();

            //Act
            await connection.CloseAsync();

            //Assert
            Assert.Null(await client.ReadLineAsync());
            await connection.WaitForCloseAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAsync("too late"));
            Assert.Equal(0, await server.BroadcastAsync("nobody"));
        }

        [Fact]
        public async Task Pushing_Over_Ssl_While_Responding_Should_Not_Corrupt_The_Stream()
        {
            //Arrange
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None) { Framing = MessageFraming.Delimiter("\n") });
            server.Mock.Send("").Receive(text => "re:" + text);
            server.Start();
            using var client = await LineClient.ConnectSslAsync(server.Port);
            var connection = await server.WaitForConnectionAsync();

            //Act
            var pushes = Enumerable.Range(0, 50).Select(i => connection.SendAsync($"push {i}"));
            var requests = Enumerable.Range(0, 50).Select(i => client.SendAsync($"req {i}"));
            await Task.WhenAll(pushes.Concat(requests));
            var lines = new string[100];
            for (var i = 0; i < lines.Length; i++)
                lines[i] = await client.ReadLineAsync();

            //Assert
            Assert.Equal(50, lines.Count(l => l.StartsWith("push ")));
            Assert.Equal(50, lines.Count(l => l.StartsWith("re:req ")));
        }

        [Fact]
        public async Task Server_State_Should_Be_Shared_By_Connections()
        {
            //Arrange
            using var server = LineServer(s =>
            {
                s.Mock.Send("LOGIN").Receive("OK").GoTo("loggedIn");
                s.Mock.InState("loggedIn").Send("LIST").Receive("a,b");
                s.Mock.Send("LIST").Receive("ERR");
            });
            using var first = await LineClient.ConnectAsync(server.Port);
            using var second = await LineClient.ConnectAsync(server.Port);

            //Act
            await first.SendAsync("LOGIN");
            await first.ReadLineAsync();
            await second.SendAsync("LIST");

            //Assert
            Assert.Equal("a,b", await second.ReadLineAsync());
            server.Should().BeInState("loggedIn");
        }

        [Fact]
        public async Task Connection_State_Should_Be_Kept_Per_Connection()
        {
            //Arrange
            using var server = LineServer(s =>
            {
                s.Mock.StateScope = StateScope.Connection;
                s.Mock.OnConnect().Receive("hello").GoTo("greeted");
                s.Mock.InState("greeted").Send("LOGIN").Receive("OK").GoTo("loggedIn");
                s.Mock.InState("loggedIn").Send("LIST").Receive("a,b");
                s.Mock.Send("").Receive("ERR");
            });
            using var first = await LineClient.ConnectAsync(server.Port);
            using var second = await LineClient.ConnectAsync(server.Port);
            await first.ReadLineAsync();
            await second.ReadLineAsync();

            //Act
            await first.SendAsync("LOGIN");
            var login = await first.ReadLineAsync();
            await first.SendAsync("LIST");
            var firstList = await first.ReadLineAsync();
            await second.SendAsync("LIST");
            var secondList = await second.ReadLineAsync();

            //Assert
            Assert.Equal("OK", login);
            Assert.Equal("a,b", firstList);
            Assert.Equal("ERR", secondList);
            var connections = server.Connections.ToDictionary(c => ((IPEndPoint)c.RemoteEndPoint).Port);
            Assert.Equal("loggedIn", connections[((IPEndPoint)first.LocalEndPoint).Port].State);
            Assert.Equal("greeted", connections[((IPEndPoint)second.LocalEndPoint).Port].State);
            Assert.Equal(Rony.Handlers.RequestHandler.InitialState, server.Mock.State);
        }

        [Fact]
        public async Task OnUnmatched_Should_Answer_And_Keep_The_Connection_Open()
        {
            //Arrange
            using var server = LineServer(s =>
            {
                s.Mock.Send("PING").Receive("PONG");
                s.Mock.OnUnmatched().Receive(text => $"ERR unknown command '{text}'");
            });
            using var client = await LineClient.ConnectAsync(server.Port);

            //Act
            await client.SendAsync("PNIG");
            var error = await client.ReadLineAsync();
            await client.SendAsync("PING");
            var pong = await client.ReadLineAsync();

            //Assert
            Assert.Equal("ERR unknown command 'PNIG'", error);
            Assert.Equal("PONG", pong);
            Assert.Single(server.Mock.UnmatchedRequests);
        }

        [Fact]
        public async Task OnUnmatched_NoReply_Should_Keep_The_Connection_Open()
        {
            //Arrange
            using var server = LineServer(s =>
            {
                s.Mock.Send("PING").Receive("PONG");
                s.Mock.OnUnmatched().NoReply();
            });
            using var client = await LineClient.ConnectAsync(server.Port);

            //Act
            await client.SendAsync("PNIG");
            await client.SendAsync("PING");

            //Assert
            Assert.Equal("PONG", await client.ReadLineAsync());
        }

        [Fact]
        public async Task FailOnUnmatched_Should_Fail_A_Wait_As_Soon_As_A_Wrong_Request_Arrives()
        {
            //Arrange
            using var server = LineServer(s =>
            {
                s.Mock.FailOnUnmatched = true;
                s.Mock.Send("HEARTBEAT").NoReply();
            });
            using var client = await LineClient.ConnectAsync(server.Port);
            var wait = server.Mock.WaitForRequestAsync("HEARTBEAT", TimeSpan.FromSeconds(30));

            //Act
            await client.SendAsync("HEARTBAET");

            //Assert
            var exception = await Assert.ThrowsAsync<MockVerificationException>(() => wait.WaitAsync(Timeout));
            Assert.Contains("\"HEARTBAET\"", exception.Message);
        }

        [Fact]
        public async Task Log_Should_Describe_What_The_Server_Did()
        {
            //Arrange
            var log = new ConcurrentQueue<string>();
            using var server = LineServer(s =>
            {
                s.Log = log.Enqueue;
                s.Mock.OnConnect().Receive("hi");
                s.Mock.Send("PING").Receive("PONG").GoTo("pinged");
                s.Mock.Send("BOOM").Receive(new Func<string, string>(_ => throw new InvalidOperationException("kaput")));
            });
            var client = await LineClient.ConnectAsync(server.Port);
            await client.ReadLineAsync();
            await client.SendAsync("PING");
            await client.ReadLineAsync();
            await client.SendAsync("BOOM");
            await client.SendAsync("NOPE");
            var connection = server.Connections.Single();

            //Act
            client.Dispose();
            await connection.WaitForCloseAsync();

            //Assert
            var lines = string.Join(Environment.NewLine, log);
            Assert.All(log, line => Assert.StartsWith("[Rony ", line));
            Assert.Contains("listening on 127.0.0.1:", lines);
            Assert.Contains("#1 connected from 127.0.0.1:", lines);
            Assert.Contains("#1 sent greeting \"hi\"", lines);
            Assert.Contains("#1 received \"PING\" (matched \"PING\")", lines);
            Assert.Contains("#1 sent \"PONG\"", lines);
            Assert.Contains("#1 state \"initial\" -> \"pinged\"", lines);
            Assert.Contains("InvalidOperationException: kaput", lines);
            Assert.Contains("#1 received \"NOPE\" (unmatched, in state \"pinged\")", lines);
            Assert.Contains("#1 disconnected", lines);
        }

        [Fact]
        public async Task Log_Should_Report_A_Failed_Tls_Handshake()
        {
            //Arrange
            var log = new ConcurrentQueue<string>();
            using var server = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None)) { Log = log.Enqueue };
            server.Start();

            //Act: speak plain text to a TLS server
            using (var client = await LineClient.ConnectAsync(server.Port))
            {
                await client.SendAsync("this is not a TLS handshake");
                await client.ReadLineAsync();
            }

            //Assert
            var deadline = DateTime.UtcNow + Timeout;
            while (!log.Any(l => l.Contains("failed")) && DateTime.UtcNow < deadline)
                await Task.Delay(20);
            Assert.Contains(log, l => l.Contains("connection from 127.0.0.1:") && l.Contains("failed"));
        }

        [Fact]
        public void A_Throwing_Log_Should_Not_Break_The_Server()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0)) { Log = _ => throw new InvalidOperationException() };

            //Act & Assert
            server.Start();
            server.Stop();
        }

        [Fact]
        public async Task Udp_Connection_State_Should_Be_Kept_Per_Client_Address()
        {
            //Arrange
            using var server = new MockServer(new UdpServer("127.0.0.1", 0));
            server.Mock.StateScope = StateScope.Connection;
            server.Mock.Send("LOGIN").Receive("OK").GoTo("in");
            server.Mock.InState("in").Send("WHO").Receive("you");
            server.Mock.Send("WHO").Receive("nobody");
            server.Start();
            using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

            //Act
            var login = await UdpRequestAsync(first, server.Port, "LOGIN");
            var firstWho = await UdpRequestAsync(first, server.Port, "WHO");
            var secondWho = await UdpRequestAsync(second, server.Port, "WHO");

            //Assert
            Assert.Equal("OK", login);
            Assert.Equal("you", firstWho);
            Assert.Equal("nobody", secondWho);
            await Assert.ThrowsAsync<NotSupportedException>(() => server.BroadcastAsync("x"));
        }

        private static async Task<string> UdpRequestAsync(UdpClient client, int port, string request)
        {
            var data = Encoding.UTF8.GetBytes(request);
            await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));
            var result = await client.ReceiveAsync().WaitAsync(Timeout);
            return Encoding.UTF8.GetString(result.Buffer);
        }

        /// <summary>A client for line-based protocols; every read times out instead of hanging a test.</summary>
        private sealed class LineClient : IDisposable
        {
            private readonly TcpClient _client;
            private readonly Stream _stream;
            private readonly StreamReader _reader;

            private LineClient(TcpClient client, Stream stream)
            {
                _client = client;
                _stream = stream;
                _reader = new StreamReader(stream, Encoding.UTF8);
            }

            public EndPoint LocalEndPoint => _client.Client.LocalEndPoint;

            public static async Task<LineClient> ConnectAsync(int port)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return new LineClient(client, client.GetStream());
            }

            public static async Task<LineClient> ConnectSslAsync(int port)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                var ssl = new SslStream(client.GetStream(), false,
                    (_, certificate, _, _) => certificate?.GetCertHashString() == TestCertificate.Instance.GetCertHashString());
                await ssl.AuthenticateAsClientAsync(TestCertificate.SubjectName);
                return new LineClient(client, ssl);
            }

            private readonly System.Threading.SemaphoreSlim _writeLock = new System.Threading.SemaphoreSlim(1, 1);

            public async Task SendAsync(string line)
            {
                var data = Encoding.UTF8.GetBytes(line + "\n");
                await _writeLock.WaitAsync();
                try
                {
                    await _stream.WriteAsync(data);
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            /// <summary>The next line, or null once the server closed the connection.</summary>
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

            public void Dispose()
            {
                _stream.Dispose();
                _client.Dispose();
            }
        }
    }
}
