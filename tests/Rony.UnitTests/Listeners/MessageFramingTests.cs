using Rony.Listeners;
using System;
using System.IO;
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

        [Theory]
        [InlineData(2, true, new byte[] { 0, 5 })]
        [InlineData(4, false, new byte[] { 7, 0, 0, 0 })]
        public void LengthPrefix_That_Includes_The_Prefix_Should_Count_It_In_The_Length(int prefixLength, bool bigEndian, byte[] expectedPrefix)
        {
            //Arrange
            var framing = MessageFraming.LengthPrefix(prefixLength, bigEndian, includesPrefix: true);
            var body = new byte[] { 7, 8, 9 };

            //Act
            var encoded = framing.Encode(body);
            var decoded = framing.Decode(encoded.Concat(encoded).ToArray(), true, out var consumed);

            //Assert
            Assert.Equal(expectedPrefix.Concat(body), encoded);
            Assert.Equal(2, decoded.Count);
            Assert.All(decoded, m => Assert.Equal(body, m));
            Assert.Equal(encoded.Length * 2, consumed);
        }

        [Fact]
        public void LengthPrefix_That_Includes_The_Prefix_Should_Reject_A_Length_Smaller_Than_The_Prefix()
        {
            //Assert
            Assert.Throws<InvalidDataException>(() => MessageFraming.LengthPrefix(2, true, true).Decode(new byte[] { 0, 1, 9 }, true, out _));
        }

        [Fact]
        public void LengthPrefix_That_Includes_The_Prefix_Should_Reject_Too_Long_Response()
        {
            //Assert
            Assert.Throws<InvalidOperationException>(() => MessageFraming.LengthPrefix(1, true, true).Encode(new byte[255]));
            Assert.Equal(256 - 1, MessageFraming.LengthPrefix(1, true, true).Encode(new byte[254]).Length);
        }

        [Fact]
        public void FixedLength_Should_Split_Messages_And_Keep_The_Remainder()
        {
            //Act
            var messages = MessageFraming.FixedLength(3).Decode("abcdef gh".GetBytes(), true, out var consumed);

            //Assert
            Assert.Equal(new[] { "abc", "def", " gh" }, messages.Select(m => m.GetString()));
            Assert.Equal(9, consumed);

            var partial = MessageFraming.FixedLength(4).Decode("abcdef".GetBytes(), true, out var consumedPartial);
            Assert.Equal(new[] { "abcd" }, partial.Select(m => m.GetString()));
            Assert.Equal(4, consumedPartial);
        }

        [Fact]
        public void FixedLength_Should_Pad_Short_Responses_And_Reject_Long_Ones()
        {
            //Arrange
            var framing = MessageFraming.FixedLength(4, (byte)' ');

            //Assert
            Assert.Equal("ab  ", framing.Encode("ab".GetBytes()).GetString());
            Assert.Equal("abcd", framing.Encode("abcd".GetBytes()).GetString());
            Assert.Throws<InvalidOperationException>(() => framing.Encode("abcde".GetBytes()));
            Assert.Equal(new byte[] { 1, 0, 0 }, MessageFraming.FixedLength(3).Encode(new byte[] { 1 }));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void FixedLength_Should_Reject_A_Length_Below_One(int length)
        {
            //Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => MessageFraming.FixedLength(length));
        }

        [Fact]
        public void StartEnd_Should_Discard_Noise_And_Return_Messages_Without_Markers()
        {
            //Act
            var messages = MessageFraming.StxEtx.Decode("xx\u0002one\u0003yy\u0002two\u0003\u0002\u0003".GetBytes(), true, out var consumed);

            //Assert
            Assert.Equal(new[] { "one", "two", "" }, messages.Select(m => m.GetString()));
            Assert.Equal(16, consumed);
        }

        [Fact]
        public void StartEnd_Should_Wait_For_The_End_Byte_Across_Calls()
        {
            //Arrange
            var framing = MessageFraming.StartEnd((byte)'<', (byte)'>');

            //Act
            var first = framing.Decode("zz<ab".GetBytes(), true, out var consumedFirst);
            var second = framing.Decode("<abc>".GetBytes(), true, out var consumedSecond);

            //Assert
            Assert.Empty(first);
            Assert.Equal(2, consumedFirst);
            Assert.Equal("abc", Assert.Single(second).GetString());
            Assert.Equal(5, consumedSecond);
        }

        [Fact]
        public void StartEnd_Should_Add_Both_Markers_To_Responses()
        {
            //Assert
            Assert.Equal("<ok>", MessageFraming.StartEnd((byte)'<', (byte)'>').Encode("ok".GetBytes()).GetString());
            Assert.Equal(new byte[] { 2, 1, 3 }, MessageFraming.StxEtx.Encode(new byte[] { 1 }));
        }

        [Fact]
        public void StartEnd_Should_Reject_Equal_Start_And_End()
        {
            //Assert
            Assert.Throws<ArgumentException>(() => MessageFraming.StartEnd(1, 1));
        }
    }
}
