using System.Net.Sockets;
using System.Text;
using Rony.Net.Testcontainers;
using Xunit;

namespace Rony.Samples;

// Runs only when RONY_TEST_IMAGE names an image of the rony tool (the CI "docker" job builds it from the Dockerfile).
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

// Wiki: Standalone-Server ("From a .NET test with Testcontainers").
public class TestcontainersSamples
{
    [DockerFact]
    public async Task A_container_started_from_a_configuration_file()
    {
        // mocks/shop.json: the file of the other scenarios; the container serves it on its own port
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "mocks"));
        var file = Path.Combine(directory, "mocks", "shop.json");
        File.WriteAllText(file, """
            {
              "version": 1,
              "rules": [
                { "request": "PING", "state": "initial", "reply": "PONG" },
                { "request": "PING", "state": "outage", "reply": "ERR" }
              ]
            }
            """);
        try
        {
            await using var rony = new RonyBuilder()
                .WithImage(Environment.GetEnvironmentVariable("RONY_TEST_IMAGE"))   // in your tests: leave this out, the default is the image of the package's version
                .WithConfigurationFile(file)                                         // in your tests: "mocks/shop.json"
                .Build();
            await rony.StartAsync();

            using var client = new TcpClient(rony.Hostname, rony.Port);
            // ... the system under test talks to rony.Hostname:rony.Port ...
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("PING"));
            var buffer = new byte[16];
            var read = await stream.ReadAsync(buffer);
            Assert.Equal("PONG", Encoding.UTF8.GetString(buffer, 0, read));

            var requests = await rony.GetReceivedRequestsAsync();
            Assert.Equal("PING", Assert.Single(requests).BodyString);

            await rony.SetStateAsync("outage");
            Assert.Equal("outage", await rony.GetStateAsync());
            await rony.ClearReceivedRequestsAsync();
            Assert.Empty(await rony.GetReceivedRequestsAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
