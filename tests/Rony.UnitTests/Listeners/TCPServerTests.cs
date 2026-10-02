using Rony.Listeners;
using System;
using System.Net;
using System.Threading;
using Xunit;

namespace Rony.Tests.Listeners
{
    public class TCPServerTests
    {
        [Fact]
        public void Constructor_Should_Work_Correctly()
        {
            //Arrange
            using var listener = new TcpServer(5000);

            //Assert
            Assert.NotNull(listener);
            Assert.Equal("127.0.0.1", listener.Address.ToString());
            Assert.Equal(5000, listener.Port);
        }

        [Fact]
        public void Constructor_With_IP_Should_Work_Correctly()
        {
            //Arrange
            using var listener = new TcpServer("127.0.0.1", 5000);

            //Assert
            Assert.NotNull(listener);
            Assert.Equal("127.0.0.1", listener.Address.ToString());
            Assert.Equal(5000, listener.Port);
        }

        [Fact]
        public void Constructor_With_IPAddress_Should_Work_Correctly()
        {
            //Arrange
            using var listener = new TcpServer(IPAddress.Parse("127.0.0.1"), 5000);

            //Assert
            Assert.NotNull(listener);
            Assert.Equal("127.0.0.1", listener.Address.ToString());
            Assert.Equal(5000, listener.Port);
        }

        [Fact]
        public void Active_Property_Should_Set_Correctly()
        {
            //Arrange
            using var listener = new TcpServer(IPAddress.Parse("127.0.0.1"), 5011);

            //Act
            listener.Start();

            //Assert
            Assert.True(listener.Active);

            //Act
            listener.Stop();

            //Assert
            Assert.False(listener.Active);
        }

        [Theory]
        [InlineData(0, 0, "chunkSize")]
        [InlineData(-1, 0, "chunkSize")]
        [InlineData(1, -1, "delay")]
        public void Chunked_SendRawAsync_Should_Reject_Invalid_Arguments(int chunkSize, int delayMilliseconds, string parameter)
        {
            //Arrange
            using var listener = new TcpServer(0);

            //Act & Assert
            var exception = Record.Exception(() =>
            {
                _ = listener.SendRawAsync(new byte[] { 1 }, null, chunkSize, TimeSpan.FromMilliseconds(delayMilliseconds), CancellationToken.None);
            });
            Assert.Equal(parameter, Assert.IsType<ArgumentOutOfRangeException>(exception).ParamName);
        }
    }
}
