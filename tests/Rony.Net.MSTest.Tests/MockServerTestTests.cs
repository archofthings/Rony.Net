using Microsoft.VisualStudio.TestTools.UnitTesting;
using Rony.Interfaces;
using Rony.Listeners;
using Rony.Net;
using Rony.Net.MSTest;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Rony.Net.MSTest.Tests
{
    [TestClass]
    public class MockServerTestTests : MockServerTest
    {
        [TestMethod]
        public async Task Client_Gets_Pong()
        {
            //Arrange
            Server.Mock.Send("PING").Receive("PONG");

            //Act
            var response = await SendAsync(Server.Port, "PING");

            //Assert
            Assert.AreEqual("PONG", response);
            Assert.IsNotNull(Server.Log);
            Server.Should().HaveReceived("PING", Times.Once());
        }

        [TestMethod]
        public void Cleanup_Should_Dispose_The_Server()
        {
            //Arrange
            var first = Server;

            //Act
            DisposeMockServer();

            //Assert
            Assert.IsFalse(first.Active);
            Assert.AreNotSame(first, Server);
        }

        [TestMethod]
        public void Cleanup_Should_Fail_On_Unmatched_Requests_When_Asked()
        {
            //Arrange
            VerifyAllRequestsMatchedAfterTest = true;
            Server.Mock.Match("nobody configured this");
            var server = Server;

            //Act & Assert
            Assert.ThrowsExactly<MockVerificationException>(DisposeMockServer);
            Assert.IsFalse(server.Active);
            VerifyAllRequestsMatchedAfterTest = false;
        }

        [TestMethod]
        public async Task LogTo_Should_Write_From_Background_Threads()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0)).LogTo(TestContext);

            //Act & Assert
            await Task.Run(() => server.Log("from another thread {0} with braces"));
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

    [TestClass]
    public class UdpMockServerTests : MockServerTest
    {
        protected override IListener CreateListener() => new UdpServer("127.0.0.1", 0);

        [TestMethod]
        public void CreateListener_Should_Choose_The_Transport()
        {
            Assert.IsTrue(Server.Active);
            Assert.ThrowsExactly<NotSupportedException>(() => Server.VerifyConnections(Times.Never()));
        }
    }
}
