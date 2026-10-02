using Rony.Handlers;
using Rony.Listeners;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Stateful-Scenarios
public class ScenarioSamples
{
    [Fact]
    public async Task Login_before_list()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("LOGIN bob").Receive("OK").GoTo("loggedIn");
        server.Mock.InState("loggedIn").Send("LIST").Receive("a,b,c");
        server.Mock.InState("loggedIn").Send("LOGOUT").Receive("BYE").GoTo(RequestHandler.InitialState);
        server.Mock.Send("LIST").Receive("ERR not logged in");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("ERR not logged in", await client.SendAndReceiveAsync("LIST"));
        Assert.Equal("OK", await client.SendAndReceiveAsync("LOGIN bob"));
        Assert.Equal("a,b,c", await client.SendAndReceiveAsync("LIST"));
        Assert.Equal("BYE", await client.SendAndReceiveAsync("LOGOUT"));
        Assert.Equal("ERR not logged in", await client.SendAndReceiveAsync("LIST"));
    }

    [Fact]
    public void Read_and_set_the_state()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.InState("maintenance").Send("").Receive("503 down for maintenance");
        server.Mock.Send("").Receive("200 OK");

        Assert.Equal("initial", server.Mock.State);
        server.Mock.State = "maintenance";   // start the test in a state

        Assert.Equal("503 down for maintenance", server.Mock.Match("GET /").GetString());
        server.Should().BeInState("maintenance");
    }

    [Fact]
    public void A_job_that_finishes()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.Send("START").Receive("STARTED").GoTo("running");
        server.Mock.InState("running").Send("STATUS")
            .Receive("RUNNING 10%")
            .Then("RUNNING 60%")
            .Then("DONE").GoTo("finished");
        server.Mock.InState("finished").Send("STATUS").Receive("DONE");
        server.Mock.Send("STATUS").Receive("IDLE");

        Assert.Equal("IDLE", server.Mock.Match("STATUS").GetString());
        server.Mock.Match("START");
        Assert.Equal("RUNNING 10%", server.Mock.Match("STATUS").GetString());
        Assert.Equal("RUNNING 60%", server.Mock.Match("STATUS").GetString());
        Assert.Equal("DONE", server.Mock.Match("STATUS").GetString());
        Assert.Equal("finished", server.Mock.State);
    }

    [Fact]
    public async Task A_session_per_connection()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.StateScope = StateScope.Connection;
        server.Mock.Send("LOGIN bob").Receive("OK").GoTo("loggedIn");
        server.Mock.InState("loggedIn").Send("LIST").Receive("a,b,c");
        server.Mock.Send("LIST").Receive("ERR not logged in");
        server.Start();

        using var bob = await TcpTestClient.ConnectAsync(server.Port);
        using var stranger = await TcpTestClient.ConnectAsync(server.Port);

        await bob.SendAndReceiveAsync("LOGIN bob");
        Assert.Equal("a,b,c", await bob.SendAndReceiveAsync("LIST"));
        Assert.Equal("ERR not logged in", await stranger.SendAndReceiveAsync("LIST"));

        var connections = server.Connections;
        Assert.Equal("loggedIn", connections[0].State);
        Assert.Equal("initial", connections[1].State);
    }

    [Fact]
    public async Task Greeting_starts_the_session()
    {
        using var server = new MockServer(new TcpServer(0));
        server.Mock.StateScope = StateScope.Connection;
        server.Mock.OnConnect().Receive("+OK POP3 ready").GoTo("authorization");
        server.Mock.InState("authorization").Send("USER bob").Receive("+OK");
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("+OK POP3 ready", await client.ReceiveAsync());
        Assert.Equal("+OK", await client.SendAndReceiveAsync("USER bob"));
    }
}
