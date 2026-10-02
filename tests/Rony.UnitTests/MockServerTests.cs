using Rony.Listeners;
using Rony.Net;
using System;
using Xunit;

namespace Rony.Tests
{
    public class MockServerTests
    {
        [Fact]
        public void Constructor_Should_Work_Correctly()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(5000));

            //Assert
            Assert.NotNull(server);
            Assert.NotNull(server.Mock);
            Assert.Equal("127.0.0.1", server.Address.ToString());
            Assert.Equal(5000, server.Port);
        }

        [Fact]
        public void Active_Property_Should_Set_Correctly()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));

            //Act
            server.Start();

            //Assert
            Assert.True(server.Active);

            //Act
            server.Stop();

            //Assert
            Assert.False(server.Active);
        }

        [Fact]
        public void Server_Should_Be_Stop_After_Dispose()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));

            //Act
            server.Start();

            //Assert
            Assert.True(server.Active);

            //Act
            server.Dispose();

            //Assert
            Assert.False(server.Active);

        }

        [Fact]
        public void Server_Should_Return_Error_On_Adding_Duplicate_Request()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(5003));
            var request = new byte[] { 1, 2, 3 };

            //Act
            server.Mock.Send(request).Receive(new byte[] { 3, 4, 5 });

            //Assert
            Assert.Throws<ArgumentException>(() => server.Mock.Send(request).Receive("test"));
        }

        [Fact]
        public void Server_Should_Ignore_Duplicate_Start_And_Stop()
        {
            //Arrange
            using var server = new MockServer(new TcpServer(0));

            //Act
            server.Start();
            server.Start();

            //Assert
            Assert.True(server.Active);

            //Act
            server.Stop();
            server.Stop();

            //Assert
            Assert.False(server.Active);
        }

        [Fact]
        public void Server_Should_Be_Restartable()
        {
            //Arrange
            using var server = new MockServer(new UdpServer(0));

            //Act
            server.Start();
            server.Stop();
            server.Start();

            //Assert
            Assert.True(server.Active);
        }

        [Fact]
        public void Constructor_Should_Throw_On_Null_Listener()
        {
            //Assert
            Assert.Throws<ArgumentNullException>(() => new MockServer(null));
        }
    }
}
