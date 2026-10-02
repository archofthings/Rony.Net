using NUnit.Framework;
using Rony.Interfaces;
using Rony.Listeners;
using Rony.Net;
using Rony.Net.NUnit;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Rony.Net.NUnit.Tests
{
    // NUnit 4.6 needs C# 13 to choose between its delegate overloads, so the assertions use typed delegate variables.
    public class MockServerTestTests : MockServerTest
    {
        [Test]
        public async Task Client_Gets_Pong()
        {
            //Arrange
            Server.Mock.Send("PING").Receive("PONG");

            //Act
            var response = await SendAsync(Server.Port, "PING");

            //Assert
            Assert.That(response, Is.EqualTo("PONG"));
            Assert.That(Server.Log, Is.Not.Null);
            Server.Should().HaveReceived("PING", Times.Once());
        }

        [Test]
        public void Every_Test_Should_Get_A_New_Server()
        {
            //Arrange
            var first = Server;

            //Act
            DisposeMockServer();

            //Assert
            Assert.That(first.Active, Is.False);
            Assert.That(Server, Is.Not.SameAs(first));
            Assert.That(Server.Active, Is.True);
        }

        [Test]
        public void TearDown_Should_Fail_On_Unmatched_Requests_When_Asked()
        {
            //Arrange
            VerifyAllRequestsMatchedAfterTest = true;
            Server.Mock.Match("nobody configured this");
            var server = Server;

            //Act & Assert
            Action dispose = DisposeMockServer;
            Assert.That(dispose, Throws.TypeOf<MockVerificationException>());
            Assert.That(server.Active, Is.False);
            VerifyAllRequestsMatchedAfterTest = false;
        }

        [Test]
        public void LogToTestContext_Should_Write_From_Background_Threads()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0)).LogToTestContext();

            //Act & Assert
            Func<Task> log = () => Task.Run(() => server.Log("from another thread {0} with braces"));
            Assert.That(log, Throws.Nothing);
        }

        private static async Task<string> SendAsync(int port, string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await stream.WriteAsync(request.GetBytes());
            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            return buffer[..read].GetString();
        }
    }

    public class UdpMockServerTests : MockServerTest
    {
        protected override IListener CreateListener() => new UdpServer("127.0.0.1", 0);

        [Test]
        public void CreateListener_Should_Choose_The_Transport()
        {
            Assert.That(Server.Active, Is.True);
            Action verify = () => Server.VerifyConnections(Times.Never());
            Assert.That(verify, Throws.TypeOf<NotSupportedException>());
        }
    }
}
