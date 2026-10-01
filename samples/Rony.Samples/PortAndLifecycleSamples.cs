using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Ports-and-Lifecycle
public class PortAndLifecycleSamples
{
    [Fact]
    public void Port_zero_picks_a_free_port()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Start();

        Assert.InRange(server.Port, 1, 65535);
    }

    [Fact]
    public async Task Restart_keeps_the_port()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("ping").Receive("pong");

        server.Start();
        var port = server.Port;
        server.Stop();
        server.Start();

        Assert.Equal(port, server.Port);
        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("pong", await client.SendAndReceiveAsync("ping"));
    }

    [Fact]
    public async Task Many_servers_side_by_side()
    {
        var servers = Enumerable.Range(0, 5).Select(_ => new MockServer(new TcpServer(0))).ToList();
        try
        {
            foreach (var (server, index) in servers.Select((s, i) => (s, i)))
            {
                server.Mock.Send("who").Receive($"server {index}");
                server.Start();
            }

            for (var i = 0; i < servers.Count; i++)
            {
                using var client = await TcpTestClient.ConnectAsync(servers[i].Port);
                Assert.Equal($"server {i}", await client.SendAndReceiveAsync("who"));
            }
        }
        finally
        {
            servers.ForEach(s => s.Dispose());
        }
    }

    [Fact]
    public void Start_and_stop_are_safe_to_repeat()
    {
        using var server = new MockServer(new TcpServer(0));

        server.Start();
        server.Start();   // no-op
        server.Stop();
        server.Stop();    // no-op

        Assert.False(server.Active);
    }

    [Fact]
    public async Task Configure_after_start()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Start();

        server.Mock.Send("late").Receive("still works");

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("still works", await client.SendAndReceiveAsync("late"));
    }
}
