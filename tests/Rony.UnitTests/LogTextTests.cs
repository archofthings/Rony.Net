using Rony.Helpers;
using Xunit;

namespace Rony.UnitTests
{
    public class LogTextTests
    {
        [Theory]
        [InlineData("plain text", "plain text")]
        [InlineData("a\nb", "a\\x0Ab")]
        [InlineData("a\r\nb", "a\\x0D\\x0Ab")]
        [InlineData("\u001b[31mred", "\\x1B[31mred")]
        [InlineData("a\u202Eb", "a\\u202Eb")]
        [InlineData("a\u2028b\u2029", "a\\u2028b\\u2029")]
        public void Safe_Should_Escape_Control_Separator_And_Format_Characters(string text, string expected)
        {
            Assert.Equal(expected, LogText.Safe(text));
        }

        [Fact]
        public void Safe_Should_Cap_Long_Text()
        {
            var result = LogText.Safe(new string('x', 1000));

            Assert.Equal(LogText.MaxLength + 1, result.Length);
            Assert.EndsWith("…", result);
        }

        [Fact]
        public void Describe_Should_Escape_Format_Characters_In_Text()
        {
            Assert.Equal("\"a\\u202Eb\"", ByteFormatter.Describe(System.Text.Encoding.UTF8.GetBytes("a\u202Eb")));
        }
    }
}
