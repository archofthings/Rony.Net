using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Cli
{
    /// <summary>The whole tool without the console: <see cref="Program"/> only wires the console and the stop signals.</summary>
    internal static class CliApp
    {
        private const string Usage =
            "Usage: rony <command> [options]\n" +
            "\n" +
            "Commands:\n" +
            "  run <config.json> [options]            Run the mock server described by a configuration file\n" +
            "  validate <config.json>                 Check a configuration file without starting anything\n" +
            "  record --target <host:port> --out <file.json> [options]\n" +
            "                                         Record the traffic to a real server through a proxy\n" +
            "  replay <recording.json> [options]      Replay a recording as a mock server\n" +
            "\n" +
            "Run 'rony <command> --help' for the options of a command, 'rony --version' for the version.\n" +
            "Exit codes: 0 success, 1 runtime failure, 2 usage error or invalid file.";

        private const string FramingHelp =
            "Framing (at most one; default: none):\n" +
            "  --delimiter <text>      Messages end with this text; \\n \\r \\t \\\\ \\0 and \\xNN are interpreted\n" +
            "  --length-prefix <1|2|4> Messages start with a big-endian length of 1, 2 or 4 bytes\n" +
            "  --stx-etx               Messages are wrapped in STX (0x02) and ETX (0x03)";

        private static readonly Dictionary<string, string> CommandHelp = new Dictionary<string, string>
        {
            ["run"] =
                "Usage: rony run <config.json> [options]\n" +
                "\n" +
                "Starts the mock server described by the file (see the Configuration Files wiki page), prints where it listens\n" +
                "and logs every connection and request, until Ctrl+C or SIGTERM.\n" +
                "  --port <N>       Port (default: from the file; 0 is a free port)\n" +
                "  --address <ip>   Address (default: from the file)\n" +
                "  --journal <file> Append every received request to the file, one JSON object per line\n" +
                "  --keep <N>       Requests and connection records kept in memory (default 10000; 0 is unlimited)\n" +
                "  --control <N>    Control endpoint on 127.0.0.1:<N> (0 is a free port): read the received requests and the state\n" +
                "  --watch          Reload the rules when the file changes\n" +
                "  --quiet          Do not print the log lines",
            ["validate"] =
                "Usage: rony validate <config.json>\n" +
                "\n" +
                "Checks the file without starting anything or opening a socket. Prints OK, or the error with exit code 2.",
            ["record"] =
                "Usage: rony record --target <host:port> --out <file.json> [options]\n" +
                "\n" +
                "Relays TCP (or TLS) traffic to the target and records it, until Ctrl+C or SIGTERM; then saves the recording (nothing is written if no client connected).\n" +
                "  --target <host:port>   The real server (required)\n" +
                "  --out <file.json>      Where to save the recording (required)\n" +
                "  --force                Overwrite --out if it exists\n" +
                "  --port <N>             Port of the proxy (default 0: a free port)\n" +
                "  --address <ip>         Address of the proxy (default 127.0.0.1)\n" +
                "  --tls                  Clients connect to the proxy with TLS (a generated self-signed certificate)\n" +
                "  --target-tls           The proxy connects to the target with TLS, validating its certificate\n" +
                "  --target-insecure      With --target-tls: accept any certificate of the target\n" +
                "  --quiet                Do not print the log lines\n" +
                FramingHelp,
            ["replay"] =
                "Usage: rony replay <recording.json> [options]\n" +
                "\n" +
                "Serves a recording as a mock server, until Ctrl+C or SIGTERM.\n" +
                "  --port <N>             Port (default 0: a free port)\n" +
                "  --address <ip>         Address (default 127.0.0.1)\n" +
                "  --tls                  Serve TLS with a generated self-signed certificate\n" +
                "  --journal <file>       Append every received request to the file, one JSON object per line\n" +
                "  --keep <N>             Requests and connection records kept in memory (default 10000; 0 is unlimited)\n" +
                "  --control <N>          Control endpoint on 127.0.0.1:<N> (0 is a free port): read the received requests and the state\n" +
                "  --quiet                Do not print the log lines\n" +
                FramingHelp,
        };

        /// <summary>Runs one command line; returns the exit code (0 success, 1 runtime failure, 2 usage error or invalid file).</summary>
        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
        {
            var command = args.Length > 0 ? args[0] : null;
            try
            {
                if (command == null) throw new UsageException("Missing command.");
                if (command == "--help" || command == "-h" || command == "help")
                {
                    output.WriteLine(Usage);
                    return 0;
                }

                if (command == "--version")
                {
                    output.WriteLine(Version());
                    return 0;
                }

                if (!CommandHelp.TryGetValue(command, out var help))
                    throw new UsageException($"Unknown command \"{command}\".");

                var rest = args.Skip(1).ToArray();
                if (rest.Any(a => a == "--help" || a == "-h"))
                {
                    output.WriteLine(help);
                    return 0;
                }

                var sink = new Sink(output) { Error = error };
                switch (command)
                {
                    case "run": return await Commands.RunAsync(rest, sink, stop).ConfigureAwait(false);
                    case "validate": return Commands.Validate(rest, sink);
                    case "record": return await Commands.RecordAsync(rest, sink, stop).ConfigureAwait(false);
                    default: return await Commands.ReplayAsync(rest, sink, stop).ConfigureAwait(false);
                }
            }
            catch (UsageException exception)
            {
                error.WriteLine(exception.Message);
                error.WriteLine(command != null && CommandHelp.TryGetValue(command, out var commandHelp) ? commandHelp : Usage);
                return 2;
            }
            catch (InputException exception)
            {
                error.WriteLine(exception.Message);
                return 2;
            }
            catch (Exception exception)
            {
                error.WriteLine("Error: " + exception.Message);
                return 1;
            }
        }

        private static string Version()
        {
            var version = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? typeof(CliApp).Assembly.GetName().Version?.ToString() ?? "unknown";
            var plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }
    }

    /// <summary>The output writer shared by the threads of a server: one line at a time.</summary>
    internal sealed class Sink
    {
        private readonly TextWriter _writer;

        public Sink(TextWriter writer) => _writer = writer;

        public TextWriter Error { get; set; }

        public void WriteLine(string line)
        {
            lock (_writer)
                _writer.WriteLine(line);
        }

        /// <summary>A log line with the time of day; never throws, so a closed writer cannot crash a listener.</summary>
        public void Log(string line)
        {
            try
            {
                WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
            catch (Exception)
            {
                // Logging must never break the server.
            }
        }
    }
}
