using Rony.Models;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Rony.Tests.Models
{
    public class RecordingTests
    {
        private const string Valid = @"{
          ""version"": 1,
          ""connections"": [
            { ""id"": 1, ""note"": ""ignored"", ""messages"": [
              { ""from"": ""server"", ""at"": 3, ""text"": ""220 ready"" },
              { ""from"": ""client"", ""text"": ""LOGIN bob\r\n"" },
              { ""from"": ""server"", ""at"": 20, ""closed"": true }
            ] }
          ]
        }";

        [Fact]
        public void Parse_Should_Read_Messages_And_Ignore_Unknown_Properties()
        {
            var messages = Recording.Parse(Valid).Connections.Single().Messages;

            Assert.Equal(3, messages.Count);
            Assert.Equal(RecordedSource.Server, messages[0].Source);
            Assert.Equal("220 ready", messages[0].BodyString);
            Assert.Equal(TimeSpan.FromMilliseconds(3), messages[0].Offset);
            Assert.Equal("LOGIN bob\r\n", messages[1].BodyString);
            Assert.Equal(TimeSpan.Zero, messages[1].Offset);
            Assert.True(messages[2].IsClose);
            Assert.Empty(messages[2].Body);
        }

        [Theory]
        [InlineData(new byte[] { 0x48, 0x69, 0x0D, 0x0A, 0xC3, 0xA9 }, "\"text\"")]
        [InlineData(new byte[0], "\"text\": \"\"")]
        [InlineData(new byte[] { 0x00, 0x01, 0x02, 0xFF }, "\"base64\": \"AAEC/w==\"")]
        [InlineData(new byte[] { 0x41, 0x00 }, "\"base64\"")]
        public void ToJson_Should_Choose_Text_Or_Base64_And_Round_Trip(byte[] body, string expected)
        {
            var json = Recording.Parse(
                "{\"version\":1,\"connections\":[{\"id\":1,\"messages\":[" +
                $"{{\"from\":\"client\",\"at\":7,\"base64\":\"{Convert.ToBase64String(body)}\"}},{{\"from\":\"server\",\"at\":9,\"closed\":true}}]}}]}}").ToJson();

            Assert.Contains(expected, json);
            Assert.Equal(json, Recording.Parse(json).ToJson());
            var message = Recording.Parse(json).Connections[0].Messages[0];
            Assert.Equal(body, message.Body);
            Assert.Equal(TimeSpan.FromMilliseconds(7), message.Offset);
            Assert.True(Recording.Parse(json).Connections[0].Messages[1].IsClose);
        }

        [Theory]
        [InlineData("{\"version\":2,\"connections\":[]}", "version")]
        [InlineData("{\"connections\":[]}", "version")]
        [InlineData("{\"version\":1,\"connections\":[{\"messages\":[{\"text\":\"a\"}]}]}", "connection 1, message 1")]
        [InlineData("{\"version\":1,\"connections\":[{\"messages\":[{\"from\":\"client\",\"text\":\"a\",\"base64\":\"YQ==\"}]}]}", "exactly one")]
        [InlineData("{\"version\":1,\"connections\":[{\"messages\":[{\"from\":\"client\",\"base64\":\"%%\"}]}]}", "base64")]
        [InlineData("{\"version\":1,\"connections\":[{\"messages\":[{\"from\":\"client\",\"closed\":true},{\"from\":\"server\",\"text\":\"a\"}]}]}", "last message")]
        [InlineData("{\"version\":1,", "")]
        [InlineData("{\"version\":1,\"connections\":[{\"messages\":\"oops\"}]}", "messages")]
        [InlineData("{\"version\":1,\"connections\":[{\"messages\":[{\"from\":\"client\",\"at\":-1,\"text\":\"a\"}]}]}", "milliseconds")]
        [InlineData("{\"version\":1,\"connections\":[{\"messages\":[{\"from\":\"client\",\"at\":\"soon\",\"text\":\"a\"}]}]}", "milliseconds")]
        public void Parse_Should_Reject_Malformed_Recordings(string json, string problem)
        {
            var exception = Assert.Throws<FormatException>(() => Recording.Parse(json));

            Assert.Contains(problem, exception.Message);
        }

        [Fact]
        public void Save_And_Load_Should_Round_Trip_Through_A_File()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".rony.json");
            try
            {
                var recording = Recording.Parse(Valid);

                recording.Save(path);
                recording.Save(path);   // overwrites

                Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);   // no byte order mark
                Assert.Equal(recording.ToJson(), Recording.Load(path).ToJson());
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
