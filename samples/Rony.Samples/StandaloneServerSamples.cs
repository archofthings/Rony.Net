using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Standalone-Server (scenario "Share one file between the tool and your tests").
public class StandaloneServerSamples
{
    [Fact]
    public async Task One_file_for_the_tool_and_for_a_test()
    {
        // mocks/shop.json: the file of the first scenario with "port": 0
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "mocks"));
        var file = Path.Combine(directory, "mocks", "shop.json");
        File.WriteAllText(file, """
            {
              "version": 1,
              "server": { "port": 0, "framing": { "type": "delimiter", "delimiter": "\n" } },
              "onConnect": { "reply": "READY" },
              "rules": [
                { "request": "GET price:42", "reply": "19.99" },
                { "match": "^GET (\\S+)$", "reply": "NOT FOUND $1" }
              ]
            }
            """);
        try
        {
            using var server = MockServer.FromFile(file);   // the file that `rony run mocks/shop.json` serves, with "port": 0
            server.Start();

            using var client = await TcpTestClient.ConnectAsync(server.Port);
            Assert.Equal("READY\n", await client.ReceiveAsync());
            Assert.Equal("19.99\n", await client.SendAndReceiveAsync("GET price:42\n"));
            Assert.Equal("NOT FOUND color\n", await client.SendAndReceiveAsync("GET color\n"));

            server.Should().HaveReceived("GET price:42", Times.Once());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
