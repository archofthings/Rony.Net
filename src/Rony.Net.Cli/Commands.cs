using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Cli
{
    /// <summary>The four commands.</summary>
    internal static class Commands
    {
        private static readonly HashSet<string> NoValues = new HashSet<string>();
        private static readonly string[] FramingValues = { "delimiter", "length-prefix" };

        public static async Task<int> RunAsync(string[] args, Sink sink, CancellationToken stop)
        {
            var line = CommandLine.Parse(args, NoValues, new HashSet<string> { "quiet" });
            var path = line.SinglePositional("the configuration file");

            using (var server = Load(path))
            {
                ReadServerSettings(path, out var transport, out var socket);
                if (transport == "unix" && socket == null)
                    throw new InputException($"{path}: a configuration for this tool with \"transport\": \"unix\" must set server.path (the generated temporary path cannot be shown).");

                // Attached before the server can accept a connection, so a client reacting to the line below loses no log line.
                if (!line.Has("quiet")) server.Log = text => LogUnlessListening(sink, text);
                await server.StartAsync().ConfigureAwait(false);
                sink.WriteLine(transport == "unix" ? "Listening on unix " + socket : $"Listening on {transport} {Format(server.Address, server.Port)}");

                await WaitForStopAsync(stop).ConfigureAwait(false);
                await server.StopAsync().ConfigureAwait(false);
            }

            return 0;
        }

        public static int Validate(string[] args, Sink sink)
        {
            var line = CommandLine.Parse(args, NoValues, NoValues);
            MockServer server;
            try
            {
                server = Load(line.SinglePositional("the configuration file"));
            }
            catch (SocketException exception)
            {
                // A udp configuration binds its port when it is loaded.
                sink.Error.WriteLine("The file is valid, but its UDP port is in use: " + exception.Message);
                return 1;
            }

            using (server)
                sink.WriteLine("OK");
            return 0;
        }

        public static async Task<int> RecordAsync(string[] args, Sink sink, CancellationToken stop)
        {
            var values = new HashSet<string>(FramingValues) { "target", "out", "port", "address" };
            var flags = new HashSet<string> { "tls", "target-tls", "target-insecure", "stx-etx", "force", "quiet" };
            var line = CommandLine.Parse(args, values, flags);
            if (line.Positionals.Count > 0) throw new UsageException($"Unexpected argument: {line.Positionals[0]}.");
            if (!line.Has("target", out var target)) throw new UsageException("Option --target is required.");
            if (!line.Has("out", out var outPath)) throw new UsageException("Option --out is required.");
            if (line.Has("target-insecure") && !line.Has("target-tls")) throw new UsageException("--target-insecure needs --target-tls.");

            ParseTarget(target, out var host, out var targetPort);
            var address = line.Address();
            var port = line.Port();
            var framing = line.Framing();
            if (File.Exists(outPath) && !line.Has("force"))
                throw new InputException($"{outPath} already exists; use --force to overwrite it.");
            var created = ProbeOutput(outPath);
            var saved = false;

            X509Certificate2 certificate = null;
            RecordingProxy proxy = null;
            try
            {
                proxy = new RecordingProxy(address, port, host, targetPort) { Framing = framing };
                if (line.Has("tls")) proxy.Certificate = (certificate = TestCertificate.CreateSelfSigned());
                if (line.Has("target-tls"))
                {
                    proxy.TargetTls = true;
                    if (line.Has("target-insecure"))
                    {
                        proxy.TargetCertificateValidation = (_, __, ___, ____) => true;
                        sink.Error.WriteLine("Warning: --target-insecure accepts any certificate of the target.");
                    }
                }

                // Attached before the proxy can accept a connection, so a client reacting to the line below loses no log line.
                if (!line.Has("quiet")) proxy.Log = text => LogUnlessListening(sink, text);
                proxy.Start();
                sink.WriteLine($"Recording on {Format(proxy.Address, proxy.Port)} -> {target}");

                await WaitForStopAsync(stop).ConfigureAwait(false);
                await proxy.StopAsync().ConfigureAwait(false);

                Save(proxy.Recording, outPath);
                saved = true;
                var count = proxy.Recording.Connections.Count;
                sink.WriteLine($"Saved {count} connection{(count == 1 ? "" : "s")} to {outPath}");
            }
            finally
            {
                proxy?.Dispose();
                certificate?.Dispose();
                if (created && !saved) TryDelete(outPath);
            }

            return 0;
        }

        public static async Task<int> ReplayAsync(string[] args, Sink sink, CancellationToken stop)
        {
            var values = new HashSet<string>(FramingValues) { "port", "address" };
            var flags = new HashSet<string> { "tls", "stx-etx", "quiet" };
            var line = CommandLine.Parse(args, values, flags);
            var path = line.SinglePositional("the recording file");
            var address = line.Address();
            var port = line.Port();
            var framing = line.Framing();

            Recording recording;
            try
            {
                recording = Recording.Load(path);
            }
            catch (Exception exception) when (exception is FormatException || exception is IOException || exception is UnauthorizedAccessException)
            {
                throw new InputException($"{path}: {exception.Message}");
            }

            X509Certificate2 certificate = null;
            try
            {
                TcpServerBase listener;
                if (line.Has("tls"))
                {
                    certificate = TestCertificate.CreateSelfSigned();
                    listener = new TcpServerSsl(address, port, certificate, SslProtocols.None) { Framing = framing };
                }
                else
                {
                    listener = new TcpServer(address, port) { Framing = framing };
                }

                using (var server = new MockServer(listener))
                {
                    try
                    {
                        server.Replay(recording);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new InputException($"{path}: {exception.Message}");
                    }

                    // Attached before the server can accept a connection, so a client reacting to the line below loses no log line.
                    if (!line.Has("quiet")) server.Log = text => LogUnlessListening(sink, text);
                    await server.StartAsync().ConfigureAwait(false);
                    sink.WriteLine($"Listening on {(line.Has("tls") ? "tls" : "tcp")} {Format(server.Address, server.Port)}");

                    await WaitForStopAsync(stop).ConfigureAwait(false);
                    await server.StopAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                certificate?.Dispose();
            }

            return 0;
        }

        private static MockServer Load(string path)
        {
            try
            {
                return MockServer.FromFile(path);
            }
            catch (Exception exception) when (exception is FormatException || exception is IOException
                                              || exception is UnauthorizedAccessException || exception is ArgumentException)
            {
                throw new InputException($"{path}: {exception.Message}");
            }
        }

        private static async Task WaitForStopAsync(CancellationToken stop)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The stop signal is the normal way to end.
            }
        }

        /// <summary>The tool prints its own "Listening on" / "Recording on" line, so the core's equivalent trace is dropped.</summary>
        private static void LogUnlessListening(Sink sink, string text)
        {
            if (text.Contains("] listening on ") || text.Contains("] recording proxy listening on ")) return;
            sink.Log(text);
        }

        // MockServer does not expose its listener, so the transport word and the socket file come from the (valid) file itself.
        private static void ReadServerSettings(string path, out string transport, out string socket)
        {
            transport = "tcp";
            socket = null;
            try
            {
                var server = JsonData.Parse(File.ReadAllText(path))["server"];
                transport = server["transport"].AsString() ?? transport;
                socket = server["path"].AsString();
            }
            catch (Exception exception) when (exception is FormatException || exception is IOException)
            {
                // Only the wording of the line depends on it.
            }
        }

        /// <summary>Proves <paramref name="outPath"/> can be written before anything starts; true when the probe created the file.</summary>
        private static bool ProbeOutput(string outPath)
        {
            var existed = File.Exists(outPath);
            try
            {
                using (new FileStream(outPath, existed ? FileMode.Open : FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                {
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                                              || exception is ArgumentException || exception is NotSupportedException)
            {
                throw new InputException($"Cannot write {outPath}: {exception.Message}");
            }

            return !existed;
        }

        /// <summary>Saves to <c>&lt;out&gt;.tmp</c> and moves it over the target, so a failed save never destroys an existing file.</summary>
        private static void Save(Recording recording, string outPath)
        {
            var temporary = outPath + ".tmp";
            try
            {
                recording.Save(temporary);
                File.Move(temporary, outPath, true);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                // Best effort cleanup.
            }
        }

        private static string Format(IPAddress address, int port) =>
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

        private static void ParseTarget(string target, out string host, out int port)
        {
            // host:port or [ipv6]:port; a bare IPv6 address such as ::1 is ambiguous and rejected.
            var colon = target.LastIndexOf(':');
            host = colon > 0 ? target.Substring(0, colon) : "";
            if (host.StartsWith("[", StringComparison.Ordinal) && host.EndsWith("]", StringComparison.Ordinal))
                host = host.Substring(1, host.Length - 2);
            else if (host.Contains(':') || host.Contains('[') || host.Contains(']'))
                host = "";

            if (host.Length == 0 || !int.TryParse(target.Substring(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
            {
                port = 0;
                throw new UsageException($"--target must be <host>:<port> or [<ipv6>]:<port> with a port from 1 to 65535, not \"{target}\".");
            }
        }
    }
}
