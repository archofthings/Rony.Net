using System.Text.RegularExpressions;
using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

public class PartialMatchingSamples
{
    [Fact]
    public void Json_requests()   // Request-Matching
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.SendJson(j => j["type"].AsString() == "login").Receive("{\"ok\":true}");
        server.Mock.SendJson(j => j["items"].Count > 2 && j["user"]["roles"][0].AsString() == "admin").Receive("big admin order");

        Assert.Equal("{\"ok\":true}", server.Mock.Match("{\"type\":\"login\",\"user\":\"bob\"}").GetString());
        Assert.Empty(server.Mock.Match("not json"));   // not JSON: no match, and no error is logged
    }

    [Fact]
    public void Json_response_built_from_the_request()   // Request-Matching
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.SendJson(j => j["id"].Exists)
            .Receive(request => "{\"echo\":" + JsonData.Parse(request)["id"] + "}");

        Assert.Equal("{\"echo\":42}", server.Mock.Match("{\"id\":42}").GetString());
    }

    [Fact]
    public void Capture_groups()   // Request-Matching
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send(new Regex(@"^HELLO (\w+)$")).ReceiveMatch(m => $"HI {m.Groups[1].Value}");

        Assert.Equal("HI bob", server.Mock.Match("HELLO bob").GetString());
    }

    [Fact]
    public void Readme_matching_lines()   // README
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send(new Regex(@"^HELLO (\w+)$")).ReceiveMatch(m => $"HI {m.Groups[1].Value}");
        server.Mock.SendJson(j => j["type"].AsString() == "login").Receive("{\"ok\":true}");

        Assert.Equal("HI amy", server.Mock.Match("HELLO amy").GetString());
        Assert.Equal("{\"ok\":true}", server.Mock.Match("{\"type\":\"login\"}").GetString());
    }
}
