using Rony.Listeners;
using System;
using System.Linq;
using Xunit;

namespace Rony.Tests.Listeners
{
    public class MessageFramingTests
    {
        [Fact]
        public void None_Should_Return_The_Whole_Burst_As_One_Message()
        {
            //Act
            var waiting = MessageFraming.None.Decode("abc".GetBytes(), false, out var consumedWaiting);
            var messages = MessageFraming.None.Decode("abc".GetBytes(), true, out var consumed);

            //Assert
            Assert.Empty(waiting);
            Assert.Equal(0, consumedWaiting);
            Assert.Single(messages);
            Assert.Equal("abc", messages[0].GetString());
            Assert.Equal(3, consumed);
            Assert.Equal("abc", MessageFraming.None.Encode("abc".GetBytes()).GetString());
        }

        [Fact]
        public void Delimiter_Should_Split_Messages_And_Keep_The_Remainder()
        {
            //Arrange
            var framing = MessageFraming.Delimiter("\r\n");

            //Act
            var messages = framing.Decode("one\r\ntwo\r\nthr".GetBytes(), true, out var consumed);

            //Assert
            Assert.Equal(new[] { "one", "two" }, messages.Select(m => m.GetString()));
            Assert.Equal(10, consumed);
        }

        [Fact]
        public void Delimiter_Should_Return_Empty_Messages_For_Consecutive_Delimiters()
        {
            //Act
            var messages = MessageFraming.Delimiter("\n").Decode("a\n\n".GetBytes(), true, out var consumed);

            //Assert
            Assert.Equal(new[] { "a", "" }, messages.Select(m => m.GetString()));
            Assert.Equal(3, consumed);
        }

        [Fact]
        public void Delimiter_Should_Append_Delimiter_To_Response()
        {
            //Assert
            Assert.Equal("pong\n", MessageFraming.Delimiter("\n").Encode("pong".GetBytes()).GetString());
        }

        [Fact]
        public void Delimiter_Should_Reject_Empty_Delimiter()
        {
            //Assert
            Assert.Throws<ArgumentException>(() => MessageFraming.Delimiter(""));
        }

        [Theory]
        [InlineData(1, true, new byte[] { 3 })]
        [InlineData(2, true, new byte[] { 0, 3 })]
        [InlineData(2, false, new byte[] { 3, 0 })]
        [InlineData(4, true, new byte[] { 0, 0, 0, 3 })]
        [InlineData(4, false, new byte[] { 3, 0, 0, 0 })]
        public void LengthPrefix_Should_Encode_And_Decode(int prefixLength, bool bigEndian, byte[] expectedPrefix)
        {
            //Arrange
            var framing = MessageFraming.LengthPrefix(prefixLength, bigEndian);

            //Act
            var encoded = framing.Encode(new byte[] { 7, 8, 9 });
            var decoded = framing.Decode(encoded.Concat(encoded).ToArray(), true, out var consumed);

            //Assert
            Assert.Equal(expectedPrefix.Concat(new byte[] { 7, 8, 9 }), encoded);
            Assert.Equal(2, decoded.Count);
            Assert.All(decoded, m => Assert.Equal(new byte[] { 7, 8, 9 }, m));
            Assert.Equal(encoded.Length * 2, consumed);
        }

        [Fact]
        public void LengthPrefix_Should_Wait_For_A_Complete_Message()
        {
            //Act
            var messages = MessageFraming.LengthPrefix(2).Decode(new byte[] { 0, 5, 1, 2 }, true, out var consumed);

            //Assert
            Assert.Empty(messages);
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void LengthPrefix_Should_Reject_Unsupported_Prefix_Length()
        {
            //Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageFraming.LengthPrefix(3));
        }

        [Fact]
        public void LengthPrefix_Should_Reject_Too_Long_Response()
        {
            //Assert
            Assert.Throws<InvalidOperationException>(() => MessageFraming.LengthPrefix(1).Encode(new byte[256]));
        }
    }
}
