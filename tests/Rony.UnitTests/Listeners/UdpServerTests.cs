using Rony.Listeners;
using System.Net;
using Xunit;

namespace Rony.Tests.Listeners
{
    public class UdpServerTests
    {
        [Fact]
        public void Constructor_Should_Work_Correctly()
        {
            //Arrange
            using var listener = new UdpServer(5000);

            //Assert
            Assert.NotNull(listener);
            Assert.Equal("0.0.0.0", listener.Address.ToString());
            Assert.Equal(5000, listener.Port);
        }

        [Fact]
        public void Constructor_With_IP_Should_Work_Correctly()
        {
            //Arrange
            using var listener = new UdpServer("127.0.0.1", 5000);

            //Assert
            Assert.NotNull(listener);
            Assert.Equal("127.0.0.1", listener.Address.ToString());
            Assert.Equal(5000, listener.Port);
        }

        [Fact]
        public void Constructor_With_EndPoint_Should_Work_Correctly()
        {
            //Arrange
            using var listener = new UdpServer(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 5000));

            //Assert
            Assert.NotNull(listener);
            Assert.Equal("127.0.0.1", listener.Address.ToString());
            Assert.Equal(5000, listener.Port);
        }

        [Fact]
        public void Active_Property_Should_Set_Correctly()
        {
            //Arrange
            using var listener = new UdpServer(5012);

            //Assert
            Assert.False(listener.Active);

            //Act
            listener.Start();

            //Assert
            Assert.True(listener.Active);

            //Act
            listener.Stop();

            //Assert
            Assert.False(listener.Active);
        }
    }
}
