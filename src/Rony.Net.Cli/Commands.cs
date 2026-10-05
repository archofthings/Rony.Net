using Rony.Interfaces;
using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Cli
{
    /// <summary>The four commands.</summary>
    internal static class Commands
    {
        private static readonly HashSet<string> NoValues = new HashSet<string>();
        private const int MaxBufferedBytes = 16 * 1024 * 1024;
        private static readonly string[] FramingValues = { "delimiter", "length-prefix" };

        public static async Task<int> RunAsync(string[] args, Sink sink, CancellationToken stop)
        {
            var line = CommandLine.Parse(args, new HashSet<string> { "port", "address", "journal", "keep" }, new HashSet<string> { "quiet" });
            var path = line.SinglePositional("the configuration file");
            var keep = line.Keep();
            var overrides = new ConfigurationOverrides();
            if (line.Has("port", out _)) overrides.Port = line.Port();
            if (line.Has("address", out _)) overrides.Address = line.Address();

            // Opened first, so an unopenable journal fails before anything is loaded or bound.
            var journal = RequestJournalFile.Open(line, sink);
            try
            {
                return await ServeAsync(line, path, keep, overrides, journal, sink, stop).ConfigureAwait(false);
            }
            catch
            {
                journal?.Abandon();
                throw;
            }
            finally
            {
                journal?.Dispose();
            }
        }

        private static async Task<int> ServeAsync(CommandLine line, string path, int keep, ConfigurationOverrides overrides,
            RequestJournalFile journal, Sink sink, CancellationToken stop)
        {
            using (var server = Load(path, overrides))
            {
                server.Mock.MaxReceivedRequests = keep;
                server.MaxConnectionRecords = keep;
                if (journal != null) server.RequestReceived += journal.Write;
                var unix = server.Listener as UnixSocketServer;
                var transport = unix != null ? "unix" : server.Listener is UdpServer ? "udp" : server.Listener is TcpServerSsl ? "tls" : "tcp";

                // Attached before the server can accept a connection, so a client reacting to the line below loses no log line.
                if (!line.Has("quiet")) server.Log = text => LogUnlessListening(sink, text);
                try
                {
                    await server.StartAsync().ConfigureAwait(false);
                }
                catch (ArgumentOutOfRangeException) when (unix != null)
                {
                    throw new InputException($"{path}: server.path \"{unix.Path}\" is too long for a Unix socket (the limit is about 104 bytes).");
                }
                catch (SocketException exception) when (unix != null && exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    throw new InputException($"{path}: server.path \"{unix.Path}\" is already in use: a file exists at that path.");
                }

                sink.WriteLine(unix != null ? "Listening on unix " + unix.Path : $"Listening on {transport} {Format(server.Address, server.Port)}");

                await WaitForStopAsync(stop).ConfigureAwait(false);
                await server.StopAsync().ConfigureAwait(false);
            }

            return 0;
        }

        public static int Validate(string[] args, Sink sink)
        {
            var line = CommandLine.Parse(args, NoValues, NoValues);
            var path = line.SinglePositional("the configuration file");
            try
            {
                MockServer.ValidateFile(path);
            }
            catch (Exception exception) when (IsInputError(exception))
            {
                throw new InputException($"{path}: {exception.Message}");
            }

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

                if (proxy.Recording.Connections.Count == 0)
                {
                    sink.WriteLine($"No connections were recorded; {outPath} was not written.");
                    return 0;
                }

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
            var values = new HashSet<string>(FramingValues) { "port", "address", "journal", "keep" };
            var flags = new HashSet<string> { "tls", "stx-etx", "quiet" };
            var line = CommandLine.Parse(args, values, flags);
            var path = line.SinglePositional("the recording file");
            var keep = line.Keep();
            var address = line.Address();
            var port = line.Port();
            var framing = line.Framing();

            var journal = RequestJournalFile.Open(line, sink);
            try
            {
                return await ReplayServeAsync(line, path, keep, address, port, framing, journal, sink, stop).ConfigureAwait(false);
            }
            catch
            {
                journal?.Abandon();
                throw;
            }
            finally
            {
                journal?.Dispose();
            }
        }

        private static async Task<int> ReplayServeAsync(CommandLine line, string path, int keep, IPAddress address, int port, IMessageFraming framing,
            RequestJournalFile journal, Sink sink, CancellationToken stop)
        {
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
                    listener = new TcpServerSsl(address, port, certificate, SslProtocols.None) { Framing = framing, MaxBufferedBytes = MaxBufferedBytes };
                }
                else
                {
                    listener = new TcpServer(address, port) { Framing = framing, MaxBufferedBytes = MaxBufferedBytes };
                }

                using (var server = new MockServer(listener))
                {
                    server.Mock.MaxReceivedRequests = keep;
                    server.MaxConnectionRecords = keep;
                    if (journal != null) server.RequestReceived += journal.Write;
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

        private static MockServer Load(string path, ConfigurationOverrides overrides)
        {
            try
            {
                return MockServer.FromFile(path, overrides);
            }
            catch (ArgumentException exception) when (exception.ParamName == "overrides")
            {
                // The core message without its " (Parameter 'overrides')" suffix.
                var message = exception.Message;
                var suffix = message.IndexOf(" (Parameter", StringComparison.Ordinal);
                throw new InputException($"{path}: {(suffix >= 0 ? message.Substring(0, suffix) : message)}");
            }
            catch (Exception exception) when (IsInputError(exception))
            {
                throw new InputException($"{path}: {exception.Message}");
            }
        }

        private static bool IsInputError(Exception exception) =>
            exception is FormatException || exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException;

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

        /// <summary>Proves <paramref name="outPath"/> can be written before anything starts; true when the probe created the file.</summary>
        private static bool ProbeOutput(string outPath)
        {
            var existed = File.Exists(outPath);
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = existed ? FileMode.Open : FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite
                };
                // Only a mode that creates the file may set it.
                if (!existed && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (new FileStream(outPath, options))
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

        /// <summary>
        /// Saves to a new file with a random name next to <paramref name="outPath"/> and moves it over the target, so a failed save
        /// never destroys an existing file and a file or symlink at a predictable name is never followed.
        /// </summary>
        private static void Save(Recording recording, string outPath)
        {
            var bytes = new UTF8Encoding(false).GetBytes(recording.ToJson());
            var suffix = new byte[4];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(suffix);
            var temporary = outPath + "." + BitConverter.ToString(suffix).Replace("-", "").ToLowerInvariant() + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                    stream.Write(bytes, 0, bytes.Length);
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
