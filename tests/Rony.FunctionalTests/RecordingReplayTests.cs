using Rony.Interfaces;
using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Rony.FunctionalTests
{
    /// <summary><c>MockServer.Replay</c> with recordings written by hand.</summary>
    public class RecordingReplayTests
    {
        private static MockServer Replaying(string connections)
        {
            var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
            server.Replay(Recording.Parse("{\"version\":1,\"connections\":[" + connections + "]}"));
            server.Start();
            return server;
        }

        private static string Connection(params string[] messages) => "{\"messages\":[" + string.Join(",", messages) + "]}";

        private static string Server(string text) => "{\"from\":\"server\",\"text\":\"" + text + "\"}";

        private static string Client(string text) => "{\"from\":\"client\",\"text\":\"" + text + "\"}";

        private const string ServerClosed = "{\"from\":\"server\",\"closed\":true}";

        [Fact]
        public async Task Replay_Should_Send_Greeting_Replies_Sequences_And_Several_Frames()
        {
            //Arrange
            using var server = Replaying(
                Connection(Server("220 ready"), Client("NEXT"), Server("1"), Client("SILENT"), Client("LIST"), Server("a"), Server("b")) + "," +
                Connection(Server("220 ready"), Client("NEXT"), Server("2")));

            //Act
            using var client = await RecordingTestClient.ConnectAsync(server.Port);
            await client.SendAsync("NEXT");
            await client.SendAsync("SILENT");
            await client.SendAsync("LIST");
            await client.SendAsync("NEXT");
            await client.SendAsync("NEXT");

            //Assert: the greeting once, the silent request gets nothing, two frames for LIST, and the last NEXT repeats
            Assert.Equal(new[] { "220 ready", "1", "a", "b", "2", "2" },
                new[] { await client.ReadLineAsync(), await client.ReadLineAsync(), await client.ReadLineAsync(), await client.ReadLineAsync(), await client.ReadLineAsync(), await client.ReadLineAsync() });
        }

        [Fact]
        public async Task Replay_Should_Close_The_Connection_Where_The_Server_Did()
        {
            //Arrange
            using var server = Replaying(Connection(Client("BYE"), Server("bye"), ServerClosed));

            //Act
            using var client = await RecordingTestClient.ConnectAsync(server.Port);
            await client.SendAsync("BYE");

            //Assert
            Assert.Equal("bye", await client.ReadLineAsync());
            Assert.Null(await client.ReadLineAsync());
        }

        [Fact]
        public async Task Replay_Should_Disconnect_When_The_Server_Closed_Without_A_Reply()
        {
            //Arrange
            using var server = Replaying(Connection(Client("DROP"), ServerClosed));

            //Act
            using var client = await RecordingTestClient.ConnectAsync(server.Port);
            await client.SendAsync("DROP");

            //Assert
            Assert.Null(await client.ReadLineAsync());
        }

        [Fact]
        public void Replay_Should_Reject_Null()
        {
            using var server = new MockServer(new TcpServer(0));

            Assert.Throws<ArgumentNullException>(() => server.Replay(null));
        }

        [Fact]
        public async Task Replay_Should_Send_Binary_Requests_And_Replies()
        {
            //Arrange: a request and a reply that are not valid UTF-8, recorded as base64.
            using var server = Replaying(Connection(
                "{\"from\":\"client\",\"base64\":\"AAEC\"}",
                "{\"from\":\"server\",\"base64\":\"/wA=\"}"));

            //Act
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x00, 0x01, 0x02, (byte)'\n' });

            //Assert
            var reply = new byte[2];
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await stream.ReadExactlyAsync(reply, timeout.Token);
            Assert.Equal(new byte[] { 0xFF, 0x00 }, reply);
        }

        [Fact]
        public async Task Replay_Should_Send_A_Multi_Frame_Reply_With_LengthPrefix_Framing()
        {
            //Arrange
            var framing = MessageFraming.LengthPrefix();
            using var server = new MockServer(new TcpServer(0) { Framing = framing });
            server.Replay(Recording.Parse(
                "{\"version\":1,\"connections\":[{\"messages\":[" +
                "{\"from\":\"client\",\"text\":\"GO\"},{\"from\":\"server\",\"text\":\"a\"},{\"from\":\"server\",\"text\":\"b\"}" +
                "]}]}"));
            server.Start();

            //Act
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(framing.Encode("GO".GetBytes()));

            //Assert
            Assert.Equal("a", await ReceiveFramedAsync(stream, framing));
            Assert.Equal("b", await ReceiveFramedAsync(stream, framing));
        }

        private static async Task<string> ReceiveFramedAsync(System.IO.Stream stream, IMessageFraming framing)
        {
            var header = new byte[4];
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await stream.ReadExactlyAsync(header, timeout.Token);
            var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            var body = new byte[length];
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await stream.ReadExactlyAsync(body, timeout.Token);
            return body.GetString();
        }

        [Fact]
        public async Task Replay_On_A_Udp_Listener_Should_Not_Throw_And_Should_Send_The_First_Reply_Payload()
        {
            //Arrange: a multi-frame reaction; UDP can only send the first payload (there is no second write to drop silently).
            using var server = new MockServer(new UdpServer(0));
            server.Replay(Recording.Parse(
                "{\"version\":1,\"connections\":[{\"messages\":[" +
                "{\"from\":\"client\",\"text\":\"PING\"},{\"from\":\"server\",\"text\":\"PONG\"},{\"from\":\"server\",\"text\":\"EXTRA\"}" +
                "]}]}"));
            server.Start();

            //Act
            using var client = new UdpClient();
            var data = "PING".GetBytes();
            await client.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Loopback, server.Port));
            var response = await client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

            //Assert
            Assert.Equal("PONG", response.Buffer.GetString());
        }
    }
}
