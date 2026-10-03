using Rony.Models;
using System;
using System.Linq;
using System.Net;
using Xunit;

namespace Rony.UnitTests.Models
{
    public class MessageTests
    {
        [Fact]
        public void Constructor_With_String_Should_Work_Correctly()
        {
            //Arrange
            var message = new Message("Test Message", new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1000));

            //Assert
            Assert.NotNull(message);
            Assert.Equal("Test Message", message.BodyString);
            var endPoint = (IPEndPoint)message.Sender;
            Assert.Equal(1000, endPoint.Port);
            Assert.Equal("127.0.0.1", endPoint.Address.ToString());
        }

        [Fact]
        public void Constructor_With_Byte_Should_Work_Correctly()
        {
            //Arrange
            var message = new Message("Test Message".GetBytes(), new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1000));

            //Assert
            Assert.NotNull(message);
            Assert.True("Test Message".GetBytes().SequenceEqual(message.Body));
            var endPoint = (IPEndPoint)message.Sender;
            Assert.Equal(1000, endPoint.Port);
            Assert.Equal("127.0.0.1", endPoint.Address.ToString());
        }

        [Theory]
        [InlineData("Message")]
        [InlineData("12345")]
        [InlineData("*&#$$#")]
        [InlineData("Jsjh&^345656")]
        [InlineData("ABCD")]
        public void BodyString_Should_Return_Correct_Value(string input)
        {
            //Arrange
            var message = new Message(input.GetBytes(), new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1000));

            //Assert
            Assert.Equal(input, message.BodyString);
        }
    }
}
