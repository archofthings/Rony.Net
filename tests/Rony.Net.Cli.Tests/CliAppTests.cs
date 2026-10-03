using Rony.Listeners;
using Rony.Net;
using System;
using System.IO;
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
        public async Task Run_Should_Reject_A_Unix_Config_Without_A_Path()
        {
            using var directory = new TempDirectory();
            var file = directory.Write("mock.json", "{\"version\":1,\"server\":{\"transport\":\"unix\"},\"rules\":[]}");
            var error = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "run", file }, new LineWriter(), error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(2, code);
            Assert.Contains("server.path", error.Text);
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
        public async Task Validate_Should_Report_A_Udp_Port_In_Use_With_Exit_Code_1()
        {
            using var directory = new TempDirectory();
            // The same address as the file's default: Windows lets 127.0.0.1 bind next to a socket on 0.0.0.0.
            using var busy = new MockServer(new UdpServer("127.0.0.1", 0));
            busy.Start();
            var file = directory.Write("mock.json", "{\"version\":1,\"server\":{\"transport\":\"udp\",\"port\":" + busy.Port + "},\"rules\":[]}");
            var error = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "validate", file }, new LineWriter(), error, CancellationToken.None).WaitAsync(Limit);

            Assert.Equal(1, code);
            Assert.Contains("The file is valid, but its UDP port is in use", error.Text);
        }

        [Theory]
        [InlineData(WikiConfig)]
        [InlineData(DockerConfig)]
        public async Task Validate_Should_Print_OK_For_A_Valid_File(string config)
        {
            using var directory = new TempDirectory();
            var output = new LineWriter();

            var code = await CliApp.RunAsync(new[] { "validate", directory.Write("mock.json", config) }, output, new LineWriter(), CancellationToken.None);

            Assert.Equal(0, code);
            Assert.Equal("OK", output.Text);
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
            var replay = CliApp.RunAsync(new[] { "replay", recordingFile, "--delimiter", "\\n", "--quiet" }, replayOutput, new LineWriter(), stopReplay.Token);
            var listening = await replayOutput.WaitForLineAsync(l => l.StartsWith("Listening on tcp 127.0.0.1:")).WaitAsync(Limit);

            Assert.Equal("PONG", await ExchangeAsync(PortOf(listening), "PING"));
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
        [InlineData("replay", "a.json", "--port", "70000")]
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
