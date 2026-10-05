using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Rony.Cli.Tests
{
    public class CliAppTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

        // The configuration shown on the wiki page Standalone-Server.
        private const string WikiConfig = @"{
  ""version"": 1,
  ""server"": { ""transport"": ""tcp"", ""port"": 0, ""framing"": { ""type"": ""delimiter"", ""delimiter"": ""\n"" } },
  ""rules"": [ { ""request"": ""PING"", ""reply"": ""PONG"" } ]
}";

        // The Docker configuration shown on the wiki page Standalone-Server.
        private const string DockerConfig =
            "{ \"version\": 1, \"server\": { \"address\": \"0.0.0.0\", \"port\": 4000 }, \"rules\": [ { \"request\": \"PING\", \"reply\": \"PONG\" } ] }";

        // The configurations of the scenarios on the wiki page Standalone-Server.
        private const string ShopConfig = @"{
  ""version"": 1,
  ""server"": { ""port"": 4000, ""framing"": { ""type"": ""delimiter"", ""delimiter"": ""\n"" } },
  ""onConnect"": { ""reply"": ""READY"" },
  ""rules"": [
    { ""request"": ""GET price:42"", ""reply"": ""19.99"" },
    { ""match"": ""^GET (\\S+)$"", ""reply"": ""NOT FOUND $1"" }
  ]
}";

        private const string FlakyConfig = @"{
  ""version"": 1,
  ""stateScope"": ""connection"",
  ""server"": { ""port"": 4001, ""framing"": { ""type"": ""delimiter"", ""delimiter"": ""\n"" } },
  ""onConnect"": { ""reply"": ""HELLO"" },
  ""onUnmatched"": { ""reply"": ""ERR login first"" },
  ""rules"": [
    { ""request"": ""LOGIN bob secret"", ""reply"": ""OK"", ""goTo"": ""authenticated"" },
    { ""request"": ""FETCH"", ""state"": ""authenticated"",
      ""replies"": [ { ""reply"": ""ERR busy"" }, { ""reply"": ""ERR busy"" }, { ""reply"": ""DATA 42"", ""afterMs"": 2000 } ] },
    { ""request"": ""CRASH"", ""state"": ""authenticated"", ""disconnect"": true }
  ]
}";

        private const string UnixConfig = @"{
  ""version"": 1,
  ""server"": { ""transport"": ""unix"", ""path"": ""/tmp/rony-demo.sock"", ""framing"": { ""type"": ""delimiter"", ""delimiter"": ""\n"" } },
  ""rules"": [ { ""request"": ""PING"", ""reply"": ""PONG"" } ]
}";

        private const string UdpConfig = @"{
  ""version"": 1,
  ""server"": { ""transport"": ""udp"", ""port"": 5000 },
  ""rules"": [ { ""request"": ""PING"", ""reply"": ""PONG"" } ]
}";

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rony-cli-" + Guid.NewGuid().ToString("N"));

            public string Path { get; }

            public string Write(string name, string content)
            {
                Directory.CreateDirectory(Path);
                var file = System.IO.Path.Combine(Path, name);
                File.WriteAllText(file, content);
                return file;
            }

            public void Dispose()
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, true);
            }
        }

        private static int PortOf(string line) => int.Parse(Regex.Match(line, @":(\d+)(?: ->.*)?$").Groups[1].Value);

        private static async Task<string> ExchangeAsync(int port, string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadLineAsync().WaitAsync(Limit);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Run_Should_Serve_The_File_And_Log_Unless_Quiet(bool quiet)
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", WikiConfig);
            using var stop = new CancellationTokenSource();
            var output = new LineWriter();
            var error = new LineWriter();

            var run = CliApp.RunAsync(quiet ? new[] { "run", file, "--quiet" } : new[] { "run", file }, output, error, stop.Token);
            var listening = await output.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);

            Assert.Equal("PONG", await ExchangeAsync(PortOf(listening), "PING"));
            if (!quiet) await output.WaitForLineAsync(l => l.Contains("PING")).WaitAsync(Limit);
            stop.Cancel();

            Assert.Equal(0, await run.WaitAsync(Limit));
            Assert.Equal(string.Empty, error.Text);
            if (quiet) Assert.Single(output.Lines);
            else Assert.True(output.Lines.Count > 1);
        }

        [Fact]
        public async Task Run_Should_Fail_With_Exit_Code_1_When_The_Port_Is_In_Use()
        {
            using var directory = new TempDirectory();
            using var busy = new MockServer(new TcpServer(0));
            busy.Start();
            var file = directory.Write("mock.json", "{\"version\":1,\"server\":{\"port\":" + busy.Port + "},\"rules\":[]}");
            var error = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "run", file }, new LineWriter(), error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(1, code);
            Assert.NotEqual(string.Empty, error.Text);
        }

        [Theory]
        [InlineData("run")]
        [InlineData("validate")]
        public async Task Invalid_And_Missing_Files_Should_Exit_With_2(string command)
        {
            using var directory = new TempDirectory();
            var invalid = directory.Write("bad.json", "{ \"version\": 1, \"rules\": [ { \"request\": \"LIST\", \"replys\": \"a\" } ] }");

            var error = new LineWriter();
            Assert.Equal(2, await CliApp.RunAsync(new[] { command, invalid }, new LineWriter(), error, CancellationToken.None));
            Assert.Contains("unknown property \"replys\"", error.Text);

            error = new LineWriter();
            Assert.Equal(2, await CliApp.RunAsync(new[] { command, Path.Combine(directory.Path, "missing.json") }, new LineWriter(), error, CancellationToken.None));
            Assert.Contains("missing.json", error.Text);
        }

        [Fact]
        public async Task Record_Should_Reject_An_Unwritable_Out_Before_Starting_The_Proxy()
        {
            using var directory = new TempDirectory();
            var output = new LineWriter();
            var error = new LineWriter();
            var outPath = Path.Combine(directory.Path, "missing", "rec.json");

            var code = await CliApp.RunAsync(new[] { "record", "--target", "127.0.0.1:1", "--out", outPath }, output, error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(2, code);
            Assert.Contains(outPath, error.Text);
            Assert.DoesNotContain("Recording on", output.Text);
        }

        [Fact]
        public async Task Run_Should_Append_Every_Request_To_The_Journal()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", WikiConfig);
            var journal = directory.Write("journal.jsonl", "{\"old\":true}\n");
            using var stop = new CancellationTokenSource();
            var output = new LineWriter();
            var error = new LineWriter();

            var run = CliApp.RunAsync(new[] { "run", file, "--journal", journal, "--quiet" }, output, error, stop.Token);
            var listening = await output.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);
            Assert.Equal("PONG", await ExchangeAsync(PortOf(listening), "PING"));
            Assert.Null(await ExchangeAsync(PortOf(listening), "OTHER"));
            stop.Cancel();

            Assert.Equal(0, await run.WaitAsync(Limit));
            var lines = File.ReadAllLines(journal);
            Assert.Equal(3, lines.Length);
            Assert.Equal("{\"old\":true}", lines[0]);
            Assert.Equal("PING", JsonData.Parse(lines[1])["text"].AsString());
            Assert.Equal(true, JsonData.Parse(lines[1])["matched"].AsBoolean());
            Assert.Equal("OTHER", JsonData.Parse(lines[2])["text"].AsString());
            Assert.Equal(false, JsonData.Parse(lines[2])["matched"].AsBoolean());
        }

        private static string WatchConfig(string reply) =>
            "{ \"version\": 1, \"server\": { \"port\": 0, \"framing\": { \"type\": \"delimiter\", \"delimiter\": \"\\n\" } }, " +
            "\"rules\": [ { \"request\": \"PING\", \"reply\": \"" + reply + "\" } ] }";

        [Fact]
        public async Task Run_Watch_Should_Reload_A_Changed_File_Keep_The_Rules_Of_A_Broken_One_And_Recover()
        {
            var interval = ConfigWatcher.DefaultInterval;
            ConfigWatcher.DefaultInterval = TimeSpan.FromMilliseconds(20);
            try
            {
                using var directory = new TempDirectory();
                var file = directory.Write("mock.json", WatchConfig("old"));
                using var stop = new CancellationTokenSource();
                var output = new LineWriter();
                var error = new LineWriter();

                var run = CliApp.RunAsync(new[] { "run", file, "--watch", "--quiet" }, output, error, stop.Token);
                var listening = await output.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", PortOf(listening));
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                async Task<string> AskAsync()
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("PING\n"));
                    return await reader.ReadLineAsync().WaitAsync(Limit);
                }

                Assert.Equal("old", await AskAsync());

                Task NextReloadAsync()
                {
                    var seen = output.Lines.Count(l => l == "Reloaded " + file);
                    var count = 0;
                    return output.WaitForLineAsync(l => l == "Reloaded " + file && ++count > seen).WaitAsync(Limit);
                }

                var reloaded = NextReloadAsync();
                File.WriteAllText(file, WatchConfig("new"));
                await reloaded;
                Assert.Equal("new", await AskAsync());

                const string broken = "{not json";
                var message = Assert.Throws<FormatException>(() => MockServer.ValidateJson(broken)).Message;
                var rejected = error.WaitForLineAsync(l => l == $"{file}: {message}; keeping the previous rules.");
                File.WriteAllText(file, broken);
                await rejected.WaitAsync(Limit);
                Assert.Equal("new", await AskAsync());

                reloaded = NextReloadAsync();
                File.WriteAllText(file, WatchConfig("again"));
                await reloaded;
                Assert.Equal("again", await AskAsync());

                stop.Cancel();
                Assert.Equal(0, await run.WaitAsync(Limit));
            }
            finally
            {
                ConfigWatcher.DefaultInterval = interval;
            }
        }

        private sealed class ControlClient : IDisposable
        {
            private readonly TcpClient _client = new TcpClient();
            private StreamReader _reader;
            private StreamWriter _writer;

            public static async Task<ControlClient> ConnectAsync(int port)
            {
                var control = new ControlClient();
                await control._client.ConnectAsync("127.0.0.1", port);
                var stream = control._client.GetStream();
                control._reader = new StreamReader(stream, Encoding.UTF8);
                control._writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                return control;
            }

            public async Task<JsonData> SendAsync(string command)
            {
                await _writer.WriteLineAsync(command);
                return JsonData.Parse(await _reader.ReadLineAsync().WaitAsync(Limit));
            }

            public void Dispose() => _client.Dispose();
        }

        private const string StateConfig = @"{
  ""version"": 1,
  ""server"": { ""port"": 0, ""framing"": { ""type"": ""delimiter"", ""delimiter"": ""\n"" } },
  ""rules"": [
    { ""request"": ""WHO"", ""reply"": ""anonymous"" },
    { ""request"": ""WHO"", ""state"": ""authenticated"", ""reply"": ""bob"" }
  ]
}";

        [Fact]
        public async Task Run_Should_Let_The_Control_Endpoint_Read_And_Clear_The_Requests()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", WikiConfig);
            using var stop = new CancellationTokenSource();
            var output = new LineWriter();

            var run = CliApp.RunAsync(new[] { "run", file, "--control", "0", "--quiet" }, output, new LineWriter(), stop.Token);
            var listening = await output.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);
            var controlLine = await output.WaitForLineAsync(l => l.StartsWith("Control on 127.0.0.1:")).WaitAsync(Limit);
            var lines = output.Lines.ToList();
            Assert.True(lines.IndexOf(listening) < lines.IndexOf(controlLine));
            Assert.Equal("PONG", await ExchangeAsync(PortOf(listening), "PING"));
            Assert.Null(await ExchangeAsync(PortOf(listening), "OTHER"));

            using (var control = await ControlClient.ConnectAsync(PortOf(controlLine)))
            {
                var all = await control.SendAsync("{\"command\":\"requests\"}");
                Assert.Equal(true, all["ok"].AsBoolean());
                Assert.Equal(2, all["requests"].Count);
                Assert.Equal(1, all["requests"][0]["seq"].AsNumber());
                Assert.Equal("PING", all["requests"][0]["text"].AsString());
                Assert.Equal(2, all["requests"][1]["seq"].AsNumber());
                Assert.Equal("OTHER", all["requests"][1]["text"].AsString());
                Assert.Equal(2, all["last"].AsNumber());

                var after = await control.SendAsync("{\"command\":\"requests\",\"after\":1}");
                Assert.Equal(1, after["requests"].Count);
                Assert.Equal("OTHER", after["requests"][0]["text"].AsString());

                Assert.Equal(true, (await control.SendAsync("{\"command\":\"clear\"}"))["ok"].AsBoolean());
                var cleared = await control.SendAsync("{\"command\":\"requests\"}");
                Assert.Equal(0, cleared["requests"].Count);
                Assert.Equal(2, cleared["last"].AsNumber());

                // the sequence keeps counting after clear
                Assert.Equal("PONG", await ExchangeAsync(PortOf(listening), "PING"));
                var next = await control.SendAsync("{\"command\":\"requests\"}");
                Assert.Equal(1, next["requests"].Count);
                Assert.Equal(3, next["requests"][0]["seq"].AsNumber());
                Assert.Equal(3, next["last"].AsNumber());
            }

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Fact]
        public async Task Run_Should_Let_The_Control_Endpoint_Read_And_Set_The_State()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", StateConfig);
            using var stop = new CancellationTokenSource();
            var output = new LineWriter();

            var run = CliApp.RunAsync(new[] { "run", file, "--control", "0", "--quiet" }, output, new LineWriter(), stop.Token);
            var listening = await output.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);
            var controlLine = await output.WaitForLineAsync(l => l.StartsWith("Control on 127.0.0.1:")).WaitAsync(Limit);

            using (var control = await ControlClient.ConnectAsync(PortOf(controlLine)))
            {
                Assert.Equal("initial", (await control.SendAsync("{\"command\":\"state\"}"))["state"].AsString());
                Assert.Equal("anonymous", await ExchangeAsync(PortOf(listening), "WHO"));
                Assert.Equal("authenticated", (await control.SendAsync("{\"command\":\"state\",\"set\":\"authenticated\"}"))["state"].AsString());
                Assert.Equal("bob", await ExchangeAsync(PortOf(listening), "WHO"));
            }

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Theory]
        [InlineData("{not json")]
        [InlineData("[1]")]
        [InlineData("{}")]
        [InlineData("{\"command\":\"bogus\"}")]
        [InlineData("{\"command\":\"requests\",\"after\":-1}")]
        [InlineData("{\"command\":\"requests\",\"after\":1.5}")]
        [InlineData("{\"command\":\"clear\",\"typo\":1}")]
        [InlineData("{\"command\":\"state\",\"set\":\"\"}")]
        public async Task Control_Errors_Should_Be_Replies_That_Keep_The_Connection_Open(string badCommand)
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", WikiConfig);
            using var stop = new CancellationTokenSource();
            var output = new LineWriter();

            var run = CliApp.RunAsync(new[] { "run", file, "--control", "0", "--quiet" }, output, new LineWriter(), stop.Token);
            var controlLine = await output.WaitForLineAsync(l => l.StartsWith("Control on 127.0.0.1:")).WaitAsync(Limit);

            using (var control = await ControlClient.ConnectAsync(PortOf(controlLine)))
            {
                var reply = await control.SendAsync(badCommand);
                Assert.Equal(false, reply["ok"].AsBoolean());
                Assert.False(string.IsNullOrEmpty(reply["error"].AsString()));
                Assert.Equal(true, (await control.SendAsync("{\"command\":\"state\"}"))["ok"].AsBoolean());
            }

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Fact]
        public async Task Control_Should_Refuse_The_State_With_Per_Connection_State()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", FlakyConfig.Replace("\"port\": 4001", "\"port\": 0"));
            using var stop = new CancellationTokenSource();
            var output = new LineWriter();

            var run = CliApp.RunAsync(new[] { "run", file, "--control", "0", "--quiet" }, output, new LineWriter(), stop.Token);
            var controlLine = await output.WaitForLineAsync(l => l.StartsWith("Control on 127.0.0.1:")).WaitAsync(Limit);

            using (var control = await ControlClient.ConnectAsync(PortOf(controlLine)))
            {
                foreach (var command in new[] { "{\"command\":\"state\"}", "{\"command\":\"state\",\"set\":\"x\"}" })
                {
                    var reply = await control.SendAsync(command);
                    Assert.Equal(false, reply["ok"].AsBoolean());
                    Assert.Contains("per-connection state", reply["error"].AsString());
                }
            }

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Fact]
        public async Task Control_Should_Keep_Only_The_Newest_Entries_With_Keep_But_Count_All()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", WikiConfig);
            using var stop = new CancellationTokenSource();
            var output = new LineWriter();

            var run = CliApp.RunAsync(new[] { "run", file, "--control", "0", "--keep", "1", "--quiet" }, output, new LineWriter(), stop.Token);
            var listening = await output.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);
            var controlLine = await output.WaitForLineAsync(l => l.StartsWith("Control on 127.0.0.1:")).WaitAsync(Limit);
            Assert.Equal("PONG", await ExchangeAsync(PortOf(listening), "PING"));
            Assert.Null(await ExchangeAsync(PortOf(listening), "OTHER"));

            using (var control = await ControlClient.ConnectAsync(PortOf(controlLine)))
            {
                var all = await control.SendAsync("{\"command\":\"requests\"}");
                Assert.Equal(1, all["requests"].Count);
                Assert.Equal(2, all["requests"][0]["seq"].AsNumber());
                Assert.Equal(2, all["last"].AsNumber());
            }

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Fact]
        public async Task Run_Should_Reject_A_Control_Port_That_Is_The_Port_Of_The_Mock()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", WikiConfig);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var output = new LineWriter();
            var error = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "run", file, "--port", port.ToString(), "--control", port.ToString() }, output, error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(2, code);
            Assert.Contains($"--control must not be the port of the mock server ({port}).", error.Text);
            Assert.Equal(string.Empty, output.Text);
        }

        [Fact]
        public async Task Run_Should_Fail_With_Exit_Code_1_When_The_Control_Port_Is_In_Use()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", WikiConfig);
            using var busy = new MockServer(new TcpServer(IPAddress.Loopback, 0));
            busy.Start();
            var error = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "run", file, "--control", busy.Port.ToString(), "--quiet" }, new LineWriter(), error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(1, code);
            Assert.Contains($"Cannot start the control endpoint on 127.0.0.1:{busy.Port}:", error.Text);
        }

        [Fact]
        public async Task Run_Should_Reject_An_Unwritable_Journal_Before_Listening()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", WikiConfig);
            var output = new LineWriter();
            var error = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "run", file, "--journal", directory.Path }, output, error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(2, code);
            Assert.Contains("Cannot write " + directory.Path, error.Text);
            Assert.DoesNotContain("Listening on", output.Text);
        }

        [Fact]
        public async Task Run_Should_Delete_A_New_Empty_Journal_When_The_File_Cannot_Be_Loaded()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("bad.json", "{ not json");
            var journal = Path.Combine(directory.Path, "journal.jsonl");

            var code = await CliApp.RunAsync(new[] { "run", file, "--journal", journal }, new LineWriter(), new LineWriter(), CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(2, code);
            Assert.False(File.Exists(journal));
        }

        [Fact]
        public void A_Failing_Journal_Write_Should_Be_Reported_Once_And_Stop_The_Journal()
        {
            var error = new LineWriter();
            var sink = new Sink(new LineWriter()) { Error = error };
            using var journal = new RequestJournalFile(new FailingStream(), sink);
            var request = new ReceivedRequest(Encoding.UTF8.GetBytes("PING"), null, DateTimeOffset.Now, true);

            journal.Write(null, request);
            journal.Write(null, request);

            Assert.Single(error.Lines);
            Assert.Contains("disk full", error.Text);
        }

        private sealed class FailingStream : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override void Flush() => throw new IOException("disk full");
            public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        [Fact]
        public async Task Validate_Should_Not_Bind_The_Port_Of_A_Udp_Configuration()
        {
            using var directory = new TempDirectory();
            using var busy = new MockServer(new UdpServer("127.0.0.1", 0));
            busy.Start();
            var file = directory.Write("mock.json", "{\"version\":1,\"server\":{\"transport\":\"udp\",\"port\":" + busy.Port + "},\"rules\":[]}");
            var output = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "validate", file }, output, new LineWriter(), CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(0, code);
            Assert.Equal("OK", output.Text);
        }

        [Theory]
        [InlineData(WikiConfig)]
        [InlineData(DockerConfig)]
        [InlineData(ShopConfig)]
        [InlineData(FlakyConfig)]
        public async Task Validate_Should_Print_OK_For_A_Valid_File(string config)
        {
            using var directory = new TempDirectory();
            var output = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "validate", directory.Write("mock.json", config) }, output, new LineWriter(), CancellationToken.None);

            Assert.Equal(0, code);
            Assert.Equal("OK", output.Text);
        }

        private static async Task<(Task<int> Run, string Listening)> StartAsync(string file, CancellationToken stop, string prefix)
        {
            var output = new LineWriter();
            var run = CliApp.RunAsync(new[] { "run", file, "--quiet" }, output, new LineWriter(), stop);
            return (run, await output.WaitForLineAsync(l => l.StartsWith(prefix)).WaitAsync(Limit));
        }

        [Fact]
        public async Task Run_Should_Serve_The_Stateful_Scenario_Of_The_Wiki()
        {
            using var directory = new TempDirectory();
            using var stop = new CancellationTokenSource();
            var (run, listening) = await StartAsync(directory.Write("flaky.json", FlakyConfig.Replace("\"port\": 4001", "\"port\": 0")), stop.Token, "Listening on tcp 127.0.0.1:");

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", PortOf(listening));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            async Task<string> AskAsync(string request)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"));
                return await reader.ReadLineAsync().WaitAsync(Limit);
            }

            Assert.Equal("HELLO", await reader.ReadLineAsync().WaitAsync(Limit));
            Assert.Equal("ERR login first", await AskAsync("FETCH"));
            Assert.Equal("OK", await AskAsync("LOGIN bob secret"));
            Assert.Equal("ERR busy", await AskAsync("FETCH"));
            Assert.Equal("ERR busy", await AskAsync("FETCH"));
            await stream.WriteAsync(Encoding.UTF8.GetBytes("CRASH\n"));
            Assert.Null(await reader.ReadLineAsync().WaitAsync(Limit));   // disconnected

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Fact]
        public async Task Run_Should_Let_Port_And_Address_Override_The_File()
        {
            using var directory = new TempDirectory();
            using var stop = new CancellationTokenSource();
            var file = directory.Write("mock.json", WikiConfig.Replace("\"port\": 0", "\"port\": 1"));
            var output = new LineWriter();
            var run = CliApp.RunAsync(new[] { "run", file, "--port", "0", "--address", "127.0.0.1", "--quiet" }, output, new LineWriter(), stop.Token);
            var listening = await output.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);

            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", PortOf(listening));
            using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("PING\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("PONG", await reader.ReadLineAsync().WaitAsync(Limit));

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Fact]
        public async Task Run_Should_Reject_Port_And_Address_For_A_Unix_Config()
        {
            using var directory = new TempDirectory();
            var error = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "run", directory.Write("sock.json", UnixConfig), "--port", "0" }, new LineWriter(), error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(2, code);
            Assert.Contains("cannot be overridden", error.Text);
            Assert.DoesNotContain("Parameter", error.Text);
        }

        [Fact]
        public async Task Run_Should_Print_The_Generated_Path_Of_A_Unix_Config_Without_A_Path()
        {
            using var directory = new TempDirectory();
            using var stop = new CancellationTokenSource();
            var config = UnixConfig.Replace("\"path\": \"/tmp/rony-demo.sock\", ", "");
            var (run, listening) = await StartAsync(directory.Write("sock.json", config), stop.Token, "Listening on unix ");

            var socketPath = listening.Substring("Listening on unix ".Length);
            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
                using var stream = new NetworkStream(socket);
                await stream.WriteAsync(Encoding.UTF8.GetBytes("PING\n"));
                using var reader = new StreamReader(stream, Encoding.UTF8);
                Assert.Equal("PONG", await reader.ReadLineAsync().WaitAsync(Limit));
            }

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
            Assert.False(File.Exists(socketPath));
        }

        [Fact]
        public async Task Run_Should_Serve_The_Unix_Socket_Scenario_Of_The_Wiki()
        {
            using var directory = new TempDirectory();
            var socketPath = System.IO.Path.Combine(directory.Path, "rony.sock");
            using var stop = new CancellationTokenSource();
            var (run, listening) = await StartAsync(directory.Write("sock.json", UnixConfig.Replace("/tmp/rony-demo.sock", socketPath.Replace("\\", "\\\\"))), stop.Token, "Listening on unix ");

            Assert.Equal("Listening on unix " + socketPath, listening);
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
            using var stream = new NetworkStream(socket);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("PING\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.Equal("PONG", await reader.ReadLineAsync().WaitAsync(Limit));

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Run_Should_Reject_A_Bad_Unix_Socket_Path_With_Exit_Code_2(bool tooLong)
        {
            if (!tooLong && OperatingSystem.IsWindows()) return;   // the error for an existing path differs there

            using var directory = new TempDirectory();
            var socketPath = System.IO.Path.Combine(directory.Path, tooLong ? new string('a', 200) : "t");
            if (!tooLong && socketPath.Length > 100) return;   // would hit the too-long branch instead (macOS limit is 104)
            if (!tooLong) directory.Write("t", "not a socket");
            var file = directory.Write("sock.json", UnixConfig.Replace("/tmp/rony-demo.sock", socketPath.Replace("\\", "\\\\")));
            var error = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "run", file }, new LineWriter(), error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(2, code);
            Assert.Contains("server.path", error.Text);
            Assert.Contains(tooLong ? "too long" : "already in use", error.Text);
            if (!tooLong) Assert.Equal("not a socket", File.ReadAllText(socketPath));
        }

        [Fact]
        public async Task Run_Should_Serve_The_Udp_Scenario_Of_The_Wiki()
        {
            using var directory = new TempDirectory();
            using var stop = new CancellationTokenSource();
            var (run, listening) = await StartAsync(directory.Write("udp.json", UdpConfig.Replace("\"port\": 5000", "\"port\": 0")), stop.Token, "Listening on udp 127.0.0.1:");

            using var client = new UdpClient();
            await client.SendAsync(Encoding.UTF8.GetBytes("PING"), 4, "127.0.0.1", PortOf(listening));
            var reply = await client.ReceiveAsync().WaitAsync(Limit);
            Assert.Equal("PONG", Encoding.UTF8.GetString(reply.Buffer));

            stop.Cancel();
            Assert.Equal(0, await run.WaitAsync(Limit));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Record_Should_Not_Write_A_File_When_No_Client_Connected(bool existing)
        {
            using var directory = new TempDirectory();
            var recordingFile = Path.Combine(directory.Path, "empty.json");
            Directory.CreateDirectory(directory.Path);
            if (existing) File.WriteAllText(recordingFile, "earlier");
            using var stop = new CancellationTokenSource();
            var output = new LineWriter();
            var record = CliApp.RunAsync(new[] { "record", "--target", "127.0.0.1:1", "--out", recordingFile, "--quiet", "--force" }, output, new LineWriter(), stop.Token);
            await output.WaitForLineAsync(l => l.StartsWith("Recording on 127.0.0.1:")).WaitAsync(Limit);

            stop.Cancel();

            Assert.Equal(0, await record.WaitAsync(Limit));
            Assert.Contains($"No connections were recorded; {recordingFile} was not written.", output.Text);
            if (existing) Assert.Equal("earlier", File.ReadAllText(recordingFile));
            else Assert.False(File.Exists(recordingFile));
        }

        [Fact]
        public async Task Record_Should_Save_A_Recording_That_Replay_Serves()
        {
            using var directory = new TempDirectory();
            var recordingFile = Path.Combine(directory.Path, "login.json");
            Directory.CreateDirectory(directory.Path);
            using var target = new MockServer(new TcpServer(0) { Framing = MessageFraming.Delimiter("\n") });
            target.Mock.Send("PING").Receive("PONG");
            target.Start();

            // record
            using var stopRecording = new CancellationTokenSource();
            var recordOutput = new LineWriter();
            var record = CliApp.RunAsync(
                new[] { "record", "--target", "127.0.0.1:" + target.Port, "--out", recordingFile, "--delimiter=\\n", "--quiet" },
                recordOutput, new LineWriter(), stopRecording.Token);
            var recording = await recordOutput.WaitForLineAsync(l => l.StartsWith("Recording on 127.0.0.1:")).WaitAsync(Limit);

            Assert.Equal("PONG", await ExchangeAsync(PortOf(recording), "PING"));
            await target.WaitForAllConnectionsClosedAsync();
            stopRecording.Cancel();

            Assert.Equal(0, await record.WaitAsync(Limit));
            Assert.Contains("Saved 1 connection to", recordOutput.Text);
            Assert.Single(Rony.Models.Recording.Load(recordingFile).Connections);
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(recordingFile));

            // an existing --out is not overwritten without --force
            var error = new LineWriter();
            var args = new[] { "record", "--target", "127.0.0.1:" + target.Port, "--out", recordingFile };
            Assert.Equal(2, await CliApp.RunAsync(args, new LineWriter(), error, CancellationToken.None));
            Assert.Contains("--force", error.Text);

            // replay
            using var stopReplay = new CancellationTokenSource();
            var replayOutput = new LineWriter();
            var replay = CliApp.RunAsync(new[] { "replay", recordingFile, "--delimiter", "\\n", "--control", "0", "--quiet" }, replayOutput, new LineWriter(), stopReplay.Token);
            var listening = await replayOutput.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);

            var controlLine = await replayOutput.WaitForLineAsync(l => l.StartsWith("Control on 127.0.0.1:")).WaitAsync(Limit);

            Assert.Equal("PONG", await ExchangeAsync(PortOf(listening), "PING"));
            using (var control = await ControlClient.ConnectAsync(PortOf(controlLine)))
                Assert.Equal("PING", (await control.SendAsync("{\"command\":\"requests\"}"))["requests"][0]["text"].AsString());
            stopReplay.Cancel();
            Assert.Equal(0, await replay.WaitAsync(Limit));
        }

        [Theory]
        [InlineData]
        [InlineData("bogus")]
        [InlineData("run")]
        [InlineData("run", "a.json", "b.json")]
        [InlineData("run", "a.json", "--unknown")]
        [InlineData("run", "a.json", "--quiet=yes")]
        [InlineData("replay", "a.json", "--port")]
        [InlineData("replay", "a.json", "--watch")]
        [InlineData("run", "a.json", "--keep", "-1")]
        [InlineData("replay", "a.json", "--keep", "many")]
        [InlineData("replay", "a.json", "--port", "70000")]
        [InlineData("run", "a.json", "--control", "70000")]
        [InlineData("replay", "a.json", "--control", "abc")]
        [InlineData("replay", "a.json", "--address", "localhost")]
        [InlineData("replay", "a.json", "--delimiter", "x", "--stx-etx")]
        [InlineData("replay", "a.json", "--length-prefix", "3")]
        [InlineData("record", "--target", "nohost", "--out", "x.json")]
        [InlineData("record", "--out", "x.json")]
        [InlineData("record", "--target", "::1", "--out", "x.json")]
        [InlineData("record", "--target", "[::1]", "--out", "x.json")]
        [InlineData("record", "--target", "[::1", "--out", "x.json")]
        [InlineData("record", "--target", "h:1", "--out", "x.json", "--target-insecure")]
        public async Task Usage_Errors_Should_Exit_With_2_And_Print_The_Usage(params string[] args)
        {
            var output = new LineWriter();
            var error = new LineWriter();

            var code = await CliApp.RunAsync(args, output, error, CancellationToken.None);

            Assert.Equal(2, code);
            Assert.Equal(string.Empty, output.Text);
            Assert.Contains("Usage:", error.Text);
        }

        [Theory]
        [InlineData("--version", null, "1.")]
        [InlineData("--help", null, "Usage: rony")]
        [InlineData("replay", "--help", "--length-prefix")]
        public async Task Version_And_Help_Should_Exit_With_0(string first, string second, string expected)
        {
            var output = new LineWriter();

            var code = await CliApp.RunAsync(second == null ? new[] { first } : new[] { first, second }, output, new LineWriter(), CancellationToken.None);

            Assert.Equal(0, code);
            Assert.Contains(expected, output.Text);
            if (first == "--version") Assert.DoesNotContain("+", output.Text);
        }

        [Theory]
        [InlineData("\\n", new byte[] { 10 })]
        [InlineData("\\r\\n", new byte[] { 13, 10 })]
        [InlineData("a\\tb\\\\", new byte[] { 97, 9, 98, 92 })]
        [InlineData("\\0", new byte[] { 0 })]
        [InlineData("\\x02\\xff;", new byte[] { 2, 255, 59 })]
        public void Delimiter_Escapes_Should_Be_Interpreted(string text, byte[] expected)
        {
            Assert.Equal(expected, CommandLine.UnescapeDelimiter(text));
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc\\")]
        [InlineData("\\q")]
        [InlineData("\\x1")]
        [InlineData("\\xZZ")]
        public void Bad_Delimiter_Escapes_Should_Be_Usage_Errors(string text)
        {
            Assert.Throws<UsageException>(() => CommandLine.UnescapeDelimiter(text));
        }
    }
}
