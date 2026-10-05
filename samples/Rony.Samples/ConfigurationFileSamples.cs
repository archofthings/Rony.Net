using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Configuration-Files (and the README section "Configuration files").
// The JSON of every example is embedded here exactly as it appears in the wiki.
public class ConfigurationFileSamples
{
    private static async Task WithFileAsync(string json, Func<string, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".rony-mock.json");
        File.WriteAllText(path, json);
        try
        {
            await test(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Read_a_server_from_a_file()
    {
        await WithFileAsync("""
            { "version": 1, "rules": [ { "request": "PING", "reply": "PONG" } ] }
            """, async path =>
        {
            using var server = MockServer.FromFile(path);   // listener and rules from the file
            server.Start();

            using var client = await TcpTestClient.ConnectAsync(server.Port);
            Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));

            server.Should().HaveReceived("PING", Times.Once());
        });
    }

    [Fact]
    public async Task Load_a_file_with_overrides_and_validate_it()
    {
        await WithFileAsync("""
            { "version": 1, "server": { "port": 4000 }, "rules": [ { "request": "PING", "reply": "PONG" } ] }
            """, async path =>
        {
            MockServer.ValidateFile(path);   // throws FormatException with the place of the mistake; opens no socket

            using var server = MockServer.FromFile(path, new ConfigurationOverrides { Port = 0 });
            server.Start();

            using var client = await TcpTestClient.ConnectAsync(server.Port);
            Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));
        });
    }

    [Fact]
    public async Task Read_the_generated_path_of_a_unix_socket_server()
    {
        await WithFileAsync("""
            { "version": 1, "server": { "transport": "unix" }, "rules": [ { "request": "PING", "reply": "PONG" } ] }
            """, async path =>
        {
            using var server = MockServer.FromFile(path);
            var socketPath = ((UnixSocketServer)server.Listener).Path;
            server.Start();

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes("PING"), SocketFlags.None);
            var buffer = new byte[16];
            var read = await socket.ReceiveAsync(buffer, SocketFlags.None);
            Assert.Equal("PONG", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        });
    }

    [Fact]
    public async Task A_complete_example()
    {
        await WithFileAsync("""
            {
              "version": 1,
              "server": {
                "transport": "tcp",
                "port": 0,
                "framing": { "type": "delimiter", "delimiter": "\n" }
              },
              "onConnect": { "reply": "220 mail.test ready" },
              "onUnmatched": { "reply": "500 unknown command" },
              "rules": [
                { "request": "PING", "reply": "PONG" },
                { "match": "^HELO (\\w+)$", "reply": "250 hello $1" },
                { "json": { "type": "login", "user": { "name": "bob" } }, "reply": "{\"ok\":true}", "goTo": "authenticated" },
                { "request": "LIST", "state": "authenticated",
                  "replies": [ { "reply": "a" }, { "reply": "b", "afterMs": 50 }, { "disconnect": true } ] },
                { "request": "LIST", "reply": "530 log in first" },
                { "request": "QUIT", "reply": "221 bye", "disconnect": true }
              ]
            }
            """, async path =>
        {
            using var server = MockServer.FromFile(path);
            server.Start();

            using var client = await TcpTestClient.ConnectAsync(server.Port);
            Assert.Equal("220 mail.test ready\n", await client.ReceiveAsync());
            Assert.Equal("PONG\n", await client.SendAndReceiveAsync("PING\n"));
            Assert.Equal("250 hello bob\n", await client.SendAndReceiveAsync("HELO bob\n"));
            Assert.Equal("530 log in first\n", await client.SendAndReceiveAsync("LIST\n"));
            Assert.Equal("{\"ok\":true}\n", await client.SendAndReceiveAsync("{\"type\":\"login\",\"user\":{\"name\":\"bob\",\"id\":7}}\n"));
            Assert.Equal("a\n", await client.SendAndReceiveAsync("LIST\n"));
            Assert.Equal("b\n", await client.SendAndReceiveAsync("LIST\n"));
            Assert.Equal(string.Empty, await client.SendAndReceiveAsync("LIST\n"));   // the last reply closes the connection

            using var other = await TcpTestClient.ConnectAsync(server.Port);
            Assert.Equal("220 mail.test ready\n", await other.ReceiveAsync());
            Assert.Equal("500 unknown command\n", await other.SendAndReceiveAsync("NOPE\n"));

            server.Should().HaveReceived("PING", Times.Once());
        });
    }

    [Fact]
    public async Task Rules_in_code_work_next_to_the_rules_of_the_file()
    {
        using var server = MockServer.FromJson("""
            { "version": 1, "rules": [ { "request": "PING", "reply": "PONG" } ] }
            """);
        server.Mock.Send("EXTRA").Receive("added in code");   // the mock is the ordinary one
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));
        Assert.Equal("added in code", await client.SendAndReceiveAsync("EXTRA"));
        server.Should().HaveReceivedInOrder("PING", "EXTRA");
    }

    [Fact]
    public async Task Request_matching_kinds()
    {
        using var server = MockServer.FromJson("""
            {
              "version": 1,
              "rules": [
                { "request": "PING", "reply": "PONG" },
                { "request": { "base64": "AQID" }, "reply": { "base64": "BAUG" } },
                { "match": "^LOGIN (\\w+)$", "reply": "WELCOME $1" },
                { "json": { "type": "login", "user": { "name": "bob" } }, "reply": "{\"ok\":true}" }
              ]
            }
            """);
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));
        await client.SendAsync(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 4, 5, 6 }, await client.ReceiveBytesAsync());
        Assert.Equal("WELCOME bob", await client.SendAndReceiveAsync("LOGIN bob"));
        Assert.Equal("{\"ok\":true}", await client.SendAndReceiveAsync("{\"type\":\"login\",\"user\":{\"name\":\"bob\",\"id\":1}}"));
    }

    [Fact]
    public async Task Responses_sequences_and_states()
    {
        using var server = MockServer.FromJson("""
            {
              "version": 1,
              "stateScope": "connection",
              "rules": [
                { "request": "LOGIN", "reply": "OK", "goTo": "authenticated" },
                { "request": "LIST", "state": "authenticated",
                  "replies": [ { "reply": "first" }, { "reply": "second", "afterMs": 20 }, { "noReply": true } ] },
                { "request": "BYE", "reply": "bye", "disconnect": true }
              ]
            }
            """);
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("OK", await client.SendAndReceiveAsync("LOGIN"));
        Assert.Equal("first", await client.SendAndReceiveAsync("LIST"));
        Assert.Equal("second", await client.SendAndReceiveAsync("LIST"));
        Assert.Equal("bye", await client.SendAndReceiveAsync("BYE"));
        server.Should().HaveReceivedInOrder("LOGIN", "LIST", "LIST", "BYE");
    }

    [Fact]
    public async Task Reload_the_rules_of_a_running_server()
    {
        await WithFileAsync("""{ "version": 1, "rules": [ { "request": "PING", "reply": "old" } ] }""", async path =>
        {
            using var server = MockServer.FromFile(path);
            server.Start();
            Assert.Equal("old", await ReplyAsync(server));

            File.WriteAllText(path, """{ "version": 1, "rules": [ { "request": "PING", "reply": "new" } ] }""");
            server.ReloadFile(path);   // new rules; connections, received requests and the scenario state are kept

            Assert.Equal("new", await ReplyAsync(server));
            server.Should().HaveReceived("PING", Times.Exactly(2));
        });
    }

    private static async Task<string> ReplyAsync(MockServer server)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync("PING".GetBytes());
        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return buffer[..read].GetString();
    }

    [Fact]
    public async Task A_tls_server_with_a_generated_certificate()
    {
        using var server = MockServer.FromJson("""
            {
              "version": 1,
              "server": { "transport": "tls", "tls": { "protocol": "tls12" } },
              "rules": [ { "request": "PING", "reply": "PONG" } ]
            }
            """);
        server.Start();

        // The generated certificate is not available to the test, so the client accepts any certificate.
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("localhost");
        await ssl.WriteAsync("PING".GetBytes());
        var buffer = new byte[64];
        var read = await ssl.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("PONG", buffer[..read].GetString());
    }

    [Fact]
    public async Task A_udp_server()
    {
        using var server = MockServer.FromJson("""
            { "version": 1, "server": { "transport": "udp", "address": "127.0.0.1" },
              "rules": [ { "request": "PING", "reply": "PONG" } ] }
            """);
        server.Start();

        Assert.Equal("PONG", await UdpTestClient.SendAndReceiveAsync(server.Port, "PING"));
    }

    [Fact]
    public void A_mistake_in_the_file_is_reported_with_its_location()
    {
        var exception = Assert.Throws<FormatException>(() => MockServer.FromJson("""
            { "version": 1, "rules": [ { "request": "PING", "reply": "PONG" }, { "request": "LIST", "replys": "a" } ] }
            """));

        Assert.Equal("rules[1]: unknown property \"replys\"", exception.Message);
    }
}
