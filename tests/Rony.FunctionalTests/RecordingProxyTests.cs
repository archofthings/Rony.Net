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
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Rony.FunctionalTests
{
    /// <summary>A line based client for the recording tests; every read times out after 5 seconds.</summary>
    internal sealed class RecordingTestClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly StreamReader _reader;

        private RecordingTestClient(TcpClient client, Stream stream)
        {
            _client = client;
            _stream = stream;
            _reader = new StreamReader(stream, Encoding.UTF8);
        }

        public static async Task<RecordingTestClient> ConnectAsync(int port, bool tls = false)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            Stream stream = client.GetStream();
            if (tls)
            {
                var ssl = new SslStream(stream, false, (_, certificate, _, _) => certificate?.GetCertHashString() == TestCertificate.Instance.GetCertHashString());
                await ssl.AuthenticateAsClientAsync(TestCertificate.SubjectName);
                stream = ssl;
            }
            return new RecordingTestClient(client, stream);
        }

        public async Task SendAsync(string line)
        {
            var data = Encoding.UTF8.GetBytes(line + "\n");
            await _stream.WriteAsync(data);
            await _stream.FlushAsync();
        }

        /// <summary>The next line, or null once the connection ended.</summary>
        public async Task<string> ReadLineAsync()
        {
            try
            {
                return await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
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

    /// <summary>The recording proxy over real sockets: the real server is a mock server on port 0.</summary>
    public class RecordingProxyTests
    {
        private static MockServer LineServer(Action<MockServer> configure)
        {
            var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
            configure(server);
            server.Start();
            return server;
        }

        private static string Describe(RecordedMessage message) =>
            $"{message.Source}:{(message.IsClose ? "<closed>" : message.BodyString)}";

        [Fact]
        public async Task Recording_Should_Capture_The_Conversation_And_Replay_It()
        {
            //Arrange
            using var real = LineServer(s =>
            {
                s.Mock.OnConnect().Receive("220 ready");
                s.Mock.Send("PING").Receive("PONG");
                s.Mock.Send("HELLO bob").Receive("hi bob");
            });
            using var proxy = new RecordingProxy("127.0.0.1", real.Port) { Framing = MessageFraming.Delimiter("\n") };
            proxy.Start();

            //Act
            using (var client = await RecordingTestClient.ConnectAsync(proxy.Port))
            {
                Assert.Equal("220 ready", await client.ReadLineAsync());
                await client.SendAsync("PING");
                Assert.Equal("PONG", await client.ReadLineAsync());
                await client.SendAsync("HELLO bob");
                Assert.Equal("hi bob", await client.ReadLineAsync());
            }
            await proxy.WaitForConnectionsClosedAsync();

            //Assert
            var recorded = proxy.Recording.Connections.Single().Messages;
            Assert.Equal(new[] { "Server:220 ready", "Client:PING", "Server:PONG", "Client:HELLO bob", "Server:hi bob", "Client:<closed>" },
                recorded.Select(Describe));

            //Act: a second mock server replays the recording
            using var replay = LineServer(s => s.Replay(Recording.Parse(proxy.Recording.ToJson())));
            using var again = await RecordingTestClient.ConnectAsync(replay.Port);
            await again.SendAsync("PING");
            await again.SendAsync("HELLO bob");

            //Assert
            Assert.Equal("220 ready", await again.ReadLineAsync());
            Assert.Equal("PONG", await again.ReadLineAsync());
            Assert.Equal("hi bob", await again.ReadLineAsync());
        }

        [Fact]
        public async Task Unreachable_Target_Should_Disconnect_The_Client_And_Keep_Accepting()
        {
            //Arrange
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var log = new ConcurrentQueue<string>();
            using var proxy = new RecordingProxy("127.0.0.1", closedPort) { Log = log.Enqueue };
            proxy.Start();

            //Act
            using (var client = await RecordingTestClient.ConnectAsync(proxy.Port))
                Assert.Null(await client.ReadLineAsync());
            await proxy.WaitForConnectionsClosedAsync();
            using (var second = await RecordingTestClient.ConnectAsync(proxy.Port))
                Assert.Null(await second.ReadLineAsync());

            //Assert
            Assert.True(proxy.Active);
            Assert.Contains(log, line => line.Contains("#1 failed"));
            Assert.Equal("Server:<closed>", Describe(proxy.Recording.Connections[0].Messages.Single()));
        }

        [Fact]
        public async Task Proxy_Should_Speak_Tls_On_Both_Sides()
        {
            //Arrange
            using var real = new MockServer(new TcpServerSsl(0, TestCertificate.Instance, SslProtocols.None) { Framing = MessageFraming.Delimiter("\n") });
            real.Mock.Send("PING").Receive("PONG");
            real.Start();
            using var proxy = new RecordingProxy("localhost", real.Port)
            {
                Framing = MessageFraming.Delimiter("\n"),
                Certificate = TestCertificate.Instance,
                TargetTls = true,
                TargetCertificateValidation = (_, certificate, _, _) => certificate?.GetCertHashString() == TestCertificate.Instance.GetCertHashString()
            };
            proxy.Start();

            //Act
            using (var client = await RecordingTestClient.ConnectAsync(proxy.Port, tls: true))
            {
                await client.SendAsync("PING");
                Assert.Equal("PONG", await client.ReadLineAsync());
            }
            await proxy.WaitForConnectionsClosedAsync();

            //Assert
            Assert.Equal(new[] { "Client:PING", "Server:PONG", "Client:<closed>" },
                proxy.Recording.Connections.Single().Messages.Select(Describe));
        }

        [Fact]
        public async Task Concurrent_Connections_Should_Be_Recorded_Separately()
        {
            //Arrange
            using var real = LineServer(s => s.Mock.SendMatching(_ => true).Receive(request => "echo " + request));
            using var proxy = new RecordingProxy("127.0.0.1", real.Port) { Framing = MessageFraming.Delimiter("\n") };
            proxy.Start();

            //Act
            using (var first = await RecordingTestClient.ConnectAsync(proxy.Port))
            using (var second = await RecordingTestClient.ConnectAsync(proxy.Port))
            {
                await first.SendAsync("A");
                await second.SendAsync("B");
                Assert.Equal("echo A", await first.ReadLineAsync());
                Assert.Equal("echo B", await second.ReadLineAsync());
            }
            await proxy.WaitForConnectionsClosedAsync();

            //Assert
            var connections = proxy.Recording.Connections;
            Assert.Equal(new[] { 1, 2 }, connections.Select(c => c.Id));
            var conversations = connections.Select(c => string.Join(",", c.Messages.Select(Describe))).ToArray();
            Assert.Contains("Client:A,Server:echo A", conversations.Single(c => c.Contains("Client:A")));
            Assert.Contains("Client:B,Server:echo B", conversations.Single(c => c.Contains("Client:B")));
        }

        [Fact]
        public async Task A_Message_Split_Over_Two_Writes_Should_Be_Recorded_Once_And_Forwarded_As_It_Arrives()
        {
            //Arrange: a raw listener stands in for the real server, so partial bytes can be observed directly.
            var targetListener = new TcpListener(IPAddress.Loopback, 0);
            targetListener.Start();
            var targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
            using var proxy = new RecordingProxy("127.0.0.1", targetPort) { Framing = MessageFraming.Delimiter("\n") };
            proxy.Start();
            var acceptTask = targetListener.AcceptTcpClientAsync();

            //Act
            using var rawClient = new TcpClient();
            await rawClient.ConnectAsync(IPAddress.Loopback, proxy.Port);
            var clientStream = rawClient.GetStream();
            using var targetClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
            var targetStream = targetClient.GetStream();

            await clientStream.WriteAsync(Encoding.UTF8.GetBytes("HEL"));
            await clientStream.FlushAsync();
            var firstPart = new byte[3];
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await targetStream.ReadExactlyAsync(firstPart, timeout.Token);
            Assert.Equal("HEL", Encoding.UTF8.GetString(firstPart));

            await clientStream.WriteAsync(Encoding.UTF8.GetBytes("LO\n"));
            await clientStream.FlushAsync();
            var secondPart = new byte[3];
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await targetStream.ReadExactlyAsync(secondPart, timeout.Token);
            Assert.Equal("LO\n", Encoding.UTF8.GetString(secondPart));

            rawClient.Dispose();
            // The client's end is passed on as a half-close. Like a real server, close only after seeing it,
            // so the client is the first to close.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                Assert.Equal(0, await targetStream.ReadAsync(new byte[1], timeout.Token));
            targetClient.Dispose();
            targetListener.Stop();
            await proxy.WaitForConnectionsClosedAsync();

            //Assert
            Assert.Equal(new[] { "Client:HELLO", "Client:<closed>" },
                proxy.Recording.Connections.Single().Messages.Select(Describe));
        }

        [Fact]
        public async Task A_Client_Half_Close_Should_Be_Passed_On_And_The_Reply_Still_Relayed()
        {
            //Arrange: the raw "real server" replies only after it saw the end of the client's stream, i.e. the propagated FIN.
            var targetListener = new TcpListener(IPAddress.Loopback, 0);
            targetListener.Start();
            using var proxy = new RecordingProxy("127.0.0.1", ((IPEndPoint)targetListener.LocalEndpoint).Port) { Framing = MessageFraming.Delimiter("\n") };
            proxy.Start();
            var target = Task.Run(async () =>
            {
                using var accepted = await targetListener.AcceptTcpClientAsync();
                var stream = accepted.GetStream();
                var received = new MemoryStream();
                await stream.CopyToAsync(received);   // returns at end of stream
                await stream.WriteAsync(Encoding.UTF8.GetBytes("reply\n"));
                await stream.FlushAsync();
                return Encoding.UTF8.GetString(received.ToArray());
            });

            //Act
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.UTF8.GetBytes("request\n"));
                await stream.FlushAsync();
                client.Client.Shutdown(SocketShutdown.Send);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                Assert.Equal("reply", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            }
            await proxy.WaitForConnectionsClosedAsync();
            targetListener.Stop();

            //Assert
            Assert.Equal("request\n", await target.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { "Client:request", "Server:reply", "Client:<closed>" },
                proxy.Recording.Connections.Single().Messages.Select(Describe));
        }

        [Fact]
        public async Task Leftover_Undecoded_Bytes_Should_Be_Recorded_When_The_Connection_Ends()
        {
            //Arrange
            using var real = LineServer(_ => { });
            using var proxy = new RecordingProxy("127.0.0.1", real.Port) { Framing = MessageFraming.Delimiter("\n") };
            proxy.Start();

            //Act: a request without its terminating delimiter, then the client disconnects.
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes("PARTIAL"));
                await client.GetStream().FlushAsync();
            }
            await proxy.WaitForConnectionsClosedAsync();

            //Assert
            Assert.Equal(new[] { "Client:PARTIAL", "Client:<closed>" },
                proxy.Recording.Connections.Single().Messages.Select(Describe));
        }

        [Fact]
        public async Task Stop_Should_Return_Promptly_With_Open_Connections_And_Be_Idempotent()
        {
            //Arrange: a reply confirms "FIRST" was relayed and recorded; "SECOND" never gets a reply, so the
            //connection is still open, mid-transfer, when Stop is called.
            using var real = LineServer(s => s.Mock.Send("FIRST").Receive("OK"));
            using var proxy = new RecordingProxy("127.0.0.1", real.Port) { Framing = MessageFraming.Delimiter("\n") };
            proxy.Start();
            using var client = await RecordingTestClient.ConnectAsync(proxy.Port);
            await client.SendAsync("FIRST");
            Assert.Equal("OK", await client.ReadLineAsync());
            await client.SendAsync("SECOND");

            //Act
            await Task.Run(() => proxy.Stop()).WaitAsync(TimeSpan.FromSeconds(5));
            proxy.Stop();   // idempotent

            //Assert: the recording is still usable after Stop
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".rony.json");
            try
            {
                proxy.Recording.Save(path);
                Assert.Contains("FIRST", File.ReadAllText(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Dispose_Without_Start_Should_Not_Throw()
        {
            using var proxy = new RecordingProxy("127.0.0.1", 0);

            proxy.Dispose();
        }

        [Fact]
        public async Task A_Throwing_Log_Callback_Should_Not_Break_Relaying()
        {
            //Arrange
            using var real = LineServer(s => s.Mock.Send("PING").Receive("PONG"));
            using var proxy = new RecordingProxy("127.0.0.1", real.Port)
            {
                Framing = MessageFraming.Delimiter("\n"),
                Log = _ => throw new InvalidOperationException("boom")
            };
            proxy.Start();

            //Act
            using (var client = await RecordingTestClient.ConnectAsync(proxy.Port))
            {
                await client.SendAsync("PING");
                Assert.Equal("PONG", await client.ReadLineAsync());
            }
            await proxy.WaitForConnectionsClosedAsync();

            //Assert
            Assert.Equal(new[] { "Client:PING", "Server:PONG", "Client:<closed>" },
                proxy.Recording.Connections.Single().Messages.Select(Describe));
        }

        [Fact]
        public async Task Recording_Should_Be_Readable_While_Traffic_Is_Still_Flowing()
        {
            //Arrange
            using var real = LineServer(s => s.Mock.SendMatching(_ => true).Receive(request => "echo " + request));
            using var proxy = new RecordingProxy("127.0.0.1", real.Port) { Framing = MessageFraming.Delimiter("\n") };
            proxy.Start();
            using var reading = new CancellationTokenSource();
            var reader = Task.Run(async () =>
            {
                while (!reading.IsCancellationRequested)
                {
                    _ = proxy.Recording.ToJson();
                    _ = proxy.Recording.Connections;
                }
            });

            //Act
            using (var client = await RecordingTestClient.ConnectAsync(proxy.Port))
            {
                for (var i = 0; i < 20; i++)
                {
                    await client.SendAsync("X" + i);
                    Assert.Equal("echo X" + i, await client.ReadLineAsync());
                }
            }
            await proxy.WaitForConnectionsClosedAsync();
            reading.Cancel();
            await reader;

            //Assert: reading while recording never threw, and the final snapshot is consistent
            Assert.Equal(41, proxy.Recording.Connections.Single().Messages.Count);
        }

        [Fact]
        public async Task Failing_Framing_Should_Not_Cut_The_Connection_And_Record_The_Bytes_Raw()
        {
            //Arrange: a length byte of 0 is invalid for this framing and makes Decode throw
            using var real = new MockServer(new TcpServer(0));
            real.Mock.SendMatching(_ => true).Receive("ok");
            real.Start();
            using var proxy = new RecordingProxy("127.0.0.1", real.Port) { Framing = MessageFraming.LengthPrefix(1, true, includesPrefix: true) };
            proxy.Start();

            //Act
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
                var stream = client.GetStream();
                var reply = new byte[2];
                foreach (var request in new[] { new byte[] { 0 }, new byte[] { 7, 7 } })
                {
                    await stream.WriteAsync(request);
                    await stream.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal("ok", Encoding.UTF8.GetString(reply));
                }
            }
            await proxy.WaitForConnectionsClosedAsync();

            //Assert
            var requests = proxy.Recording.Connections.Single().Messages.Where(m => m.Source == RecordedSource.Client && !m.IsClose).ToList();
            Assert.Equal(new[] { new byte[] { 0 }, new byte[] { 7, 7 } }, requests.Select(m => m.Body));
        }

        [Fact]
        public async Task Bytes_Without_A_Complete_Message_Should_Be_Recorded_Raw_Above_The_Cap()
        {
            //Arrange: the prefix announces 32 MiB, so the message never completes before the cap
            // (a length prefix decodes in constant time per read, unlike a delimiter that rescans the buffer)
            using var real = new MockServer(new TcpServer(0));
            real.Mock.SendMatching(_ => true).Receive("ok");
            real.Start();
            using var proxy = new RecordingProxy("127.0.0.1", real.Port) { Framing = MessageFraming.LengthPrefix(4) };
            proxy.Start();
            var data = new byte[16 * 1024 * 1024 + 1];
            data[0] = 0x02; // big-endian length 0x02000000 = 32 MiB

            //Act
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
                var stream = client.GetStream();
                await stream.WriteAsync(data);
                // Finish sending and read every reply to the end, so closing never resets the connection
                // while bytes are still on their way to the real server.
                client.Client.Shutdown(SocketShutdown.Send);
                await stream.CopyToAsync(Stream.Null).WaitAsync(TimeSpan.FromSeconds(30));
            }
            await proxy.WaitForConnectionsClosedAsync();
            await real.WaitForAllConnectionsClosedAsync();

            //Assert
            Assert.Equal(16 * 1024 * 1024 + 1, real.Mock.ReceivedRequests.Sum(r => r.Body.Length));
            var requests = proxy.Recording.Connections.Single().Messages.Where(m => m.Source == RecordedSource.Client && !m.IsClose).ToList();
            Assert.Equal(16 * 1024 * 1024 + 1, requests.Sum(m => m.Body.Length));
        }
    }
}
