using Rony.Interfaces;
using Rony.Listeners;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace Rony.Cli
{
    /// <summary>The arguments of one command: <c>--name value</c>, <c>--name=value</c>, flags and positional arguments.</summary>
    internal sealed class CommandLine
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _positionals = new List<string>();

        private CommandLine()
        {
        }

        public IReadOnlyList<string> Positionals => _positionals;

        public bool Has(string flag) => _flags.Contains(flag);

        public bool Has(string name, out string value) => _values.TryGetValue(name, out value);

        /// <summary>Parses <paramref name="args"/> against the options a command knows (names without the leading dashes).</summary>
        public static CommandLine Parse(IReadOnlyList<string> args, ISet<string> valueOptions, ISet<string> flagOptions)
        {
            var result = new CommandLine();
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
                {
                    result._positionals.Add(arg);
                    continue;
                }

                var name = arg.Substring(2);
                string value = null;
                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    value = name.Substring(equals + 1);
                    name = name.Substring(0, equals);
                }

                if (flagOptions.Contains(name))
                {
                    if (value != null) throw new UsageException($"Option --{name} does not take a value.");
                    result._flags.Add(name);
                }
                else if (valueOptions.Contains(name))
                {
                    if (value == null)
                    {
                        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                            throw new UsageException($"Option --{name} needs a value.");
                        value = args[++i];
                    }

                    if (result._values.ContainsKey(name)) throw new UsageException($"Option --{name} is given more than once.");
                    result._values[name] = value;
                }
                else
                {
                    throw new UsageException($"Unknown option --{name}.");
                }
            }

            return result;
        }

        /// <summary>The single positional argument, named <paramref name="what"/> in the error message.</summary>
        public string SinglePositional(string what)
        {
            if (_positionals.Count == 0) throw new UsageException($"Missing argument: {what}.");
            if (_positionals.Count > 1) throw new UsageException($"Unexpected argument: {_positionals[1]}.");
            return _positionals[0];
        }

        public int Port()
        {
            if (!_values.TryGetValue("port", out var text)) return 0;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
                throw new UsageException($"--port must be a number from 0 to 65535, not \"{text}\".");
            return port;
        }

        /// <summary>The port of the control endpoint (<c>--control</c>); null when the option is not given.</summary>
        public int? Control()
        {
            if (!_values.TryGetValue("control", out var text)) return null;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
                throw new UsageException($"--control must be a number from 0 to 65535, not \"{text}\".");
            return port;
        }

        /// <summary>The address of the control endpoint (<c>--control-address</c>); 127.0.0.1 by default. It needs <c>--control</c>.</summary>
        public IPAddress ControlAddress()
        {
            if (!_values.TryGetValue("control-address", out var text)) return IPAddress.Loopback;
            if (!IPAddress.TryParse(text, out var address))
                throw new UsageException($"--control-address must be an IP address, not \"{text}\".");
            if (!_values.ContainsKey("control")) throw new UsageException("--control-address needs --control.");
            return address;
        }

        /// <summary>How many received requests and connection records the server keeps (<c>--keep</c>); 10000 by default, 0 is unlimited.</summary>
        public int Keep()
        {
            if (!_values.TryGetValue("keep", out var text)) return 10000;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var keep))
                throw new UsageException($"--keep must be a number from 0 to 2147483647, not \"{text}\".");
            return keep;
        }

        public IPAddress Address()
        {
            if (!_values.TryGetValue("address", out var text)) return IPAddress.Loopback;
            if (!IPAddress.TryParse(text, out var address))
                throw new UsageException($"--address must be an IP address, not \"{text}\".");
            return address;
        }

        /// <summary>The framing from <c>--delimiter</c>, <c>--length-prefix</c> or <c>--stx-etx</c> (at most one); none by default.</summary>
        public IMessageFraming Framing()
        {
            var count = (_values.ContainsKey("delimiter") ? 1 : 0) + (_values.ContainsKey("length-prefix") ? 1 : 0) + (_flags.Contains("stx-etx") ? 1 : 0);
            if (count > 1) throw new UsageException("Use at most one of --delimiter, --length-prefix and --stx-etx.");

            if (_values.TryGetValue("delimiter", out var delimiter))
                return MessageFraming.Delimiter(UnescapeDelimiter(delimiter));
            if (_values.TryGetValue("length-prefix", out var prefix))
            {
                if (prefix != "1" && prefix != "2" && prefix != "4")
                    throw new UsageException($"--length-prefix must be 1, 2 or 4, not \"{prefix}\".");
                return MessageFraming.LengthPrefix(int.Parse(prefix, CultureInfo.InvariantCulture));
            }

            return _flags.Contains("stx-etx") ? MessageFraming.StxEtx : MessageFraming.None;
        }

        /// <summary>The text with the escapes \n, \r, \t, \\, \0 and \xNN replaced; UTF-8 encoded.</summary>
        public static byte[] UnescapeDelimiter(string text)
        {
            if (text.Length == 0) throw new UsageException("--delimiter must not be empty.");

            var bytes = new List<byte>();
            var plain = new StringBuilder();

            void FlushPlain()
            {
                if (plain.Length == 0) return;
                bytes.AddRange(Encoding.UTF8.GetBytes(plain.ToString()));
                plain.Clear();
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\\')
                {
                    plain.Append(text[i]);
                    continue;
                }

                if (i + 1 >= text.Length) throw new UsageException("--delimiter ends with a single backslash; write \\\\ for one.");
                var next = text[++i];
                switch (next)
                {
                    case 'n': plain.Append('\n'); break;
                    case 'r': plain.Append('\r'); break;
                    case 't': plain.Append('\t'); break;
                    case '\\': plain.Append('\\'); break;
                    case '0': plain.Append('\0'); break;
                    case 'x':
                        if (i + 2 >= text.Length)
                            throw new UsageException("--delimiter: \\x needs two hex digits.");
                        if (!byte.TryParse(text.Substring(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
                            throw new UsageException("--delimiter: \\x needs two hex digits.");
                        FlushPlain();
                        bytes.Add(b);
                        i += 2;
                        break;
                    default:
                        throw new UsageException($"--delimiter: unknown escape \\{next}.");
                }
            }

            FlushPlain();
            return bytes.ToArray();
        }
    }
}
