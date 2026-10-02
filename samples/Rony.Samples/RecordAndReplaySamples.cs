using System.Security.Authentication;
using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using Xunit;

namespace Rony.Samples;

// Wiki: Record-and-Replay
public class RecordAndReplaySamples
{
    [Fact]
    public async Task Record_against_a_server_and_replay_it()
    {
        // In the sample the "real" server is another mock server, and the file goes to a temporary path.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".rony.json");
        try
        {
            using (var real = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") }))
            {
                real.Mock.OnConnect().Receive("220 ready");
                real.Mock.Send("LOGIN bob").Receive("230 welcome");
                real.Start();

                // 1. Record against the real server
                using var proxy = new RecordingProxy("127.0.0.1", real.Port) { Framing = MessageFraming.Delimiter("\n") };
                proxy.Start();

                // ... point the client at proxy.Port and run it ...
                using (var client = await TcpTestClient.ConnectAsync(proxy.Port))
                {
                    Assert.Equal("220 ready\n", await client.ReceiveAsync());
                    Assert.Equal("230 welcome\n", await client.SendAndReceiveAsync("LOGIN bob\n"));
                }

                await proxy.WaitForConnectionsClosedAsync();   // the recording is complete
                proxy.Recording.Save(path);
            }

            // 2. Replay in tests
            using var server = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
            server.Replay(Recording.Load(path));
            server.Start();

            using var replayClient = await TcpTestClient.ConnectAsync(server.Port);
            Assert.Equal("220 ready\n", await replayClient.ReceiveAsync());
            Assert.Equal("230 welcome\n", await replayClient.SendAndReceiveAsync("LOGIN bob\n"));
            server.Should().HaveReceived("LOGIN bob", Times.Once());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Edit_a_recording_by_hand()
    {
        var recording = Recording.Parse("""
            {
              "version": 1,
              "connections": [
                {
                  "id": 1,
                  "messages": [
                    { "from": "server", "at": 3, "text": "220 ready" },
                    { "from": "client", "at": 12, "text": "LOGIN bob", "note": "unknown properties are ignored" },
                    { "from": "server", "at": 15, "base64": "AAEC/w==" },
                    { "from": "server", "at": 20, "closed": true }
                  ]
                }
              ]
            }
            """);

        using var server = new MockServer(new TcpServer(0));
        server.Replay(recording);
        server.Start();

        using var client = await TcpTestClient.ConnectAsync(server.Port);
        Assert.Equal("220 ready", await client.ReceiveAsync());
        await client.SendAsync("LOGIN bob");
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0xFF }, await client.ReceiveExactlyAsync(4));
        Assert.Empty(await client.ReceiveBytesAsync());   // the server closed the connection, as recorded
    }

    [Fact]
    public async Task Record_a_tls_server()
    {
        using var certificate = TestCertificates.CreateSelfSigned();
        using var real = new MockServer(new TcpServerSsl(0, certificate, SslProtocols.None));
        real.Mock.Send("PING").Receive("PONG");
        real.Start();

        // The proxy speaks TLS to the client with its own certificate, and TLS to the real server.
        using var proxy = new RecordingProxy("localhost", real.Port)
        {
            Certificate = certificate,
            TargetTls = true,
            TargetCertificateValidation = (_, serverCertificate, _, _) => serverCertificate?.GetCertHashString() == certificate.GetCertHashString()
        };
        proxy.Start();

        using (var client = await TcpTestClient.ConnectSslAsync(proxy.Port, certificate))
            Assert.Equal("PONG", await client.SendAndReceiveAsync("PING"));

        await proxy.WaitForConnectionsClosedAsync();
        Assert.Equal("PING", proxy.Recording.Connections[0].Messages[0].BodyString);
    }
}
