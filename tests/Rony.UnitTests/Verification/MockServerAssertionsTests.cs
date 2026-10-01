using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Tests.Verification
{
    public class MockServerAssertionsTests
    {
        [Fact]
        public void Passing_Assertions_Should_Chain()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Send("LOGIN").Receive("OK").GoTo("in");
            server.Mock.InState("in").Send("LIST").Receive("a");
            server.Mock.Match("LOGIN");
            server.Mock.Match("LIST");

            //Act & Assert
            server.Should().HaveReceived("LOGIN")
                .And.HaveReceived("LIST", Times.Once())
                .And.HaveReceived(r => r.BodyString.StartsWith("L"), Times.Exactly(2))
                .And.HaveReceived(new byte[] { (byte)'L', (byte)'I', (byte)'S', (byte)'T' })
                .And.NotHaveReceived("QUIT")
                .And.HaveReceivedInOrder("LOGIN", "LIST")
                .And.HaveNoUnmatchedRequests()
                .And.HaveAcceptedConnections(Times.Never())
                .And.BeInState("in");
        }

        [Fact]
        public void Failing_Assertions_Should_Throw()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));
            server.Mock.Match("PING");

            //Act & Assert
            Assert.Throws<MockVerificationException>(() => server.Should().HaveReceived("PONG"));
            Assert.Throws<MockVerificationException>(() => server.Should().NotHaveReceived("PING"));
            Assert.Throws<MockVerificationException>(() => server.Should().HaveReceivedInOrder("PING", "PING"));
            Assert.Throws<MockVerificationException>(() => server.Should().HaveNoUnmatchedRequests());
            Assert.Throws<MockVerificationException>(() => server.Should().HaveAcceptedConnections(Times.Once()));
            var exception = Assert.Throws<MockVerificationException>(() => server.Should().BeInState("other"));
            Assert.Contains("\"initial\"", exception.Message);
        }

        [Fact]
        public void Connection_Assertions_Should_Not_Be_Supported_For_Udp()
        {
            //Arrange
            using var server = new MockServer(new UdpServer("127.0.0.1", 0));

            //Act & Assert
            Assert.Throws<System.NotSupportedException>(() => server.Should().HaveAcceptedConnections(Times.Never()));
            Assert.Empty(server.Connections);
        }
    }
}
