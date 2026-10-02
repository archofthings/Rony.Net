using Rony.Listeners;
using Rony.Net;
using Xunit;
using Xunit.Abstractions;

namespace Rony.Samples;

// Wiki: Logging-and-Diagnostics
public class LoggingSamples
{
    private readonly ITestOutputHelper _output;

    public LoggingSamples(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Log_to_the_test_output()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Log = _output.WriteLine;          // or Console.WriteLine, or your logger
        server.Mock.Send("PING").Receive("PONG");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));
    }

    [Fact]
    public async Task The_log_shows_why_a_response_was_empty()
    {
        var log = new List<string>();
        using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
        server.Log = line => { lock (log) log.Add(line); };
        server.Mock.Send("PRICE ACME").Receive(text => decimal.Parse(text.Split(' ')[2]).ToString());   // bug: no [2]
        server.Mock.OnUnmatched().NoReply();
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        await client.SendAsync("PRICE ACME\n");
        await server.Mock.WaitForRequestAsync("PRICE ACME");
        await client.SendAsync("PRCE ACME\n");
        await server.Mock.WaitForRequestAsync("PRCE ACME");

        string all;
        lock (log) all = string.Join("\n", log);
        Assert.Contains("the response function for #1 threw IndexOutOfRangeException", all);
        Assert.Contains("#1 received \"PRCE ACME\" (unmatched)", all);
    }
}
