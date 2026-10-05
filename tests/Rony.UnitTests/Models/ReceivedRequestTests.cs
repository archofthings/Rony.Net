using Rony.Models;
using System;
using System.Net;
using System.Text;
using Xunit;

namespace Rony.Tests.Models
{
    public class ReceivedRequestTests
    {
        private static readonly DateTimeOffset Time = new DateTimeOffset(2026, 10, 4, 12, 34, 56, 789, TimeSpan.FromHours(2));

        [Fact]
        public void ToJson_Should_Escape_Text_Into_One_Line_That_Parses_Back()
        {
            var request = new ReceivedRequest(Encoding.UTF8.GetBytes("say \"hi\"\nbye"), new IPEndPoint(IPAddress.Loopback, 50123), Time, true, 1);

            var json = request.ToJson();

            Assert.DoesNotContain("\n", json);
            Assert.Equal("{\"time\":\"2026-10-04T12:34:56.789+02:00\",\"connection\":1,\"remote\":\"127.0.0.1:50123\",\"matched\":true,\"text\":\"say \\\"hi\\\"\\nbye\"}", json);
            Assert.Equal("say \"hi\"\nbye", JsonData.Parse(json)["text"].AsString());
        }

        [Theory]
        [InlineData(new byte[] { 0xFF, 0x00, 0x01 }, "\"base64\":\"/wAB\"")]
        [InlineData(new byte[0], "\"text\":\"\"")]
        public void ToJson_Should_Use_Base64_For_Binary_Bodies_And_Text_For_Empty_Ones(byte[] body, string expected)
        {
            var json = new ReceivedRequest(body, null, Time, false).ToJson();

            Assert.Equal("{\"time\":\"2026-10-04T12:34:56.789+02:00\",\"matched\":false," + expected + "}", json);
        }
    }
}
