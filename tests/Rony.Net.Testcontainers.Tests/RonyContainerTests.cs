using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rony.Models;
using Xunit;

namespace Rony.Net.Testcontainers.Tests
{
    // Runs only when RONY_TEST_IMAGE names an image of the rony tool (CI builds it from the Dockerfile).
    public sealed class DockerFactAttribute : FactAttribute
    {
        public DockerFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RONY_TEST_IMAGE")))
            {
                Skip = "set RONY_TEST_IMAGE to an image of the rony tool to run this test";
            }
        }
    }

    public class RonyContainerTests
    {
        [Theory]
        [InlineData("{\"seq\":1,\"time\":\"2026-10-04T12:34:56.789+02:00\",\"connection\":3,\"remote\":\"127.0.0.1:50123\",\"matched\":true,\"text\":\"PING\"}",
            "PING", 3, "127.0.0.1:50123", true)]
        [InlineData("{\"seq\":2,\"time\":\"2026-10-04T10:34:56.789+00:00\",\"matched\":false,\"base64\":\"AAH/\"}",
            "\0\u0001ÿ", null, null, false)]
        public void A_journal_entry_becomes_a_ReceivedRequest(string json, string bodyLatin1, int? connection, string remote, bool matched)
        {
            var request = RonyContainer.ToReceivedRequest(JsonData.Parse(json));

            Assert.Equal(Encoding.Latin1.GetBytes(bodyLatin1), request.Body);
            Assert.Equal(connection, request.ConnectionId);
            Assert.Equal(remote, (request.RemoteEndPoint as IPEndPoint)?.ToString());
            Assert.Equal(matched, request.Matched);
            Assert.Equal(DateTimeOffset.Parse(matched ? "2026-10-04T12:34:56.789+02:00" : "2026-10-04T10:34:56.789+00:00"), request.Timestamp);
        }

        [Fact]
        public void Build_needs_a_configuration_and_the_default_image_is_the_package_version()
        {
            Assert.Throws<ArgumentNullException>(() => new RonyBuilder().WithConfiguration(null));
            var error = Assert.Throws<ArgumentException>(() => new RonyBuilder().Build());
            Assert.Contains("WithConfigurationFile", error.Message);

            var version = typeof(RonyBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Assert.Equal("ghcr.io/archofthings/rony:" + version.Split('+')[0], new RonyBuilder().WithConfiguration("{}").ImageName);
        }

        [DockerFact]
        public async Task The_container_serves_the_configuration_and_answers_the_control_commands()
        {
            const string configuration = """
                {
                  "version": 1,
                  "rules": [
                    { "request": "STATUS", "state": "initial", "reply": "UP" },
                    { "request": "STATUS", "state": "outage", "reply": "DOWN" }
                  ]
                }
                """;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var rony = new RonyBuilder()
                .WithImage(Environment.GetEnvironmentVariable("RONY_TEST_IMAGE"))
                .WithConfiguration(configuration)
                .Build();
            await rony.StartAsync(limit.Token);

            Assert.Equal("UP", await Ask(rony, "STATUS", limit.Token));

            var requests = await rony.GetReceivedRequestsAsync(limit.Token);
            var received = Assert.Single(requests);
            Assert.Equal("STATUS", received.BodyString);
            Assert.True(received.Matched);

            Assert.Equal("initial", await rony.GetStateAsync(limit.Token));
            await rony.SetStateAsync("outage", limit.Token);
            Assert.Equal("outage", await rony.GetStateAsync(limit.Token));
            Assert.Equal("DOWN", await Ask(rony, "STATUS", limit.Token));

            await rony.ClearReceivedRequestsAsync(limit.Token);
            Assert.Empty(await rony.GetReceivedRequestsAsync(limit.Token));
        }

        private static async Task<string> Ask(RonyContainer rony, string request, CancellationToken token)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(rony.Hostname, rony.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), token);
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, token);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
    }
}
