using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace Rony.Cli
{
    /// <summary>
    /// The <c>--control</c> endpoint: a second mock server (on 127.0.0.1 unless <c>--control-address</c> says otherwise) that answers one JSON command per line with one JSON reply per
    /// line, so a test in any language can read the requests the tool received, clear them, and read or set the scenario state.
    /// </summary>
    internal sealed class ControlEndpoint : IDisposable
    {
        private const string KnownCommands = "requests, clear, state";
        private const string ConnectionStateError =
            "The server uses per-connection state (stateScope \"connection\"); the control endpoint reads and sets only the server-wide state.";

        private readonly MockServer _target;
        private readonly int _keep;
        private readonly int _port;
        private readonly MockServer _server;
        private readonly object _lock = new object();
        private readonly List<KeyValuePair<long, ReceivedRequest>> _entries = new List<KeyValuePair<long, ReceivedRequest>>();
        private static readonly JsonSerializerOptions QuoteOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        private long _last;

        /// <summary>Prepares the endpoint for <paramref name="target"/>; <paramref name="keep"/> caps the kept entries (0 is unlimited).</summary>
        public ControlEndpoint(MockServer target, int keep, IPAddress address, int port)
        {
            _target = target;
            _keep = keep;
            _port = port;
            Address = address;
            _server = new MockServer(new TcpServer(address, port)
            {
                Framing = MessageFraming.Delimiter("\n"),
                MaxBufferedBytes = 1024 * 1024
            });
            _server.Mock.MaxReceivedRequests = 16;
            _server.MaxConnectionRecords = 16;
            _server.Mock.Send("").Receive((Func<string, string>)Handle);
            _target.RequestReceived += OnRequest;
        }

        /// <summary>The address the endpoint listens on.</summary>
        public IPAddress Address { get; }

        /// <summary>The port the endpoint listens on (valid after <see cref="StartAsync"/>).</summary>
        public int Port => _server.Port;

        /// <summary>Starts listening on <see cref="Address"/>.</summary>
        public async Task StartAsync()
        {
            try
            {
                await _server.StartAsync().ConfigureAwait(false);
            }
            catch (SocketException exception)
            {
                throw new IOException($"Cannot start the control endpoint on {Address}:{_port}: {exception.Message}", exception);
            }
        }

        /// <summary>Stops listening and closes the control connections.</summary>
        public Task StopAsync()
        {
            _target.RequestReceived -= OnRequest;
            return _server.StopAsync();
        }

        public void Dispose()
        {
            _target.RequestReceived -= OnRequest;
            _server.Dispose();
        }

        private void OnRequest(object sender, ReceivedRequest request)
        {
            lock (_lock)
            {
                _entries.Add(new KeyValuePair<long, ReceivedRequest>(++_last, request));
                if (_keep > 0 && _entries.Count > _keep) _entries.RemoveRange(0, _entries.Count - _keep);
            }
        }

        /// <summary>The framing appends the line break to the reply.</summary>
        private string Handle(string line)
        {
            try
            {
                return Execute(line.EndsWith("\r", StringComparison.Ordinal) ? line.Substring(0, line.Length - 1) : line);
            }
            catch (Exception exception)
            {
                return Error(exception.Message);
            }
        }

        private string Execute(string line)
        {
            if (!JsonData.TryParse(line, out var json)) return Error("The command is not valid JSON.");
            if (json.Kind != JsonDataKind.Object) return Error("The command must be a JSON object.");
            var command = json["command"].AsString();
            if (command == null) return Error("The property \"command\" is missing or not a string.");

            switch (command)
            {
                case "requests":
                    return Requests(json);
                case "clear":
                    if (!OnlyProperties(json, out var unknown)) return unknown;
                    _target.Mock.ClearReceivedRequests();
                    lock (_lock)
                        _entries.Clear();
                    return "{\"ok\":true}";
                case "state":
                    return State(json);
                default:
                    return Error($"Unknown command \"{command}\"; the commands are {KnownCommands}.");
            }
        }

        private string Requests(JsonData json)
        {
            if (!OnlyProperties(json, out var unknown, "after")) return unknown;
            long after = 0;
            if (json["after"].Exists)
            {
                var number = json["after"].AsNumber();
                if (number == null || number < 0 || number > 9e15 || number != Math.Floor(number.Value))
                    return Error("The property \"after\" must be a non-negative whole number.");
                after = (long)number.Value;
            }

            KeyValuePair<long, ReceivedRequest>[] snapshot;
            long last;
            lock (_lock)
            {
                snapshot = _entries.ToArray();
                last = _last;
            }

            var builder = new StringBuilder("{\"ok\":true,\"requests\":[");
            var first = true;
            foreach (var entry in snapshot)
            {
                if (entry.Key <= after) continue;
                if (!first) builder.Append(',');
                first = false;
                var entryJson = entry.Value.ToJson();
                builder.Append("{\"seq\":").Append(entry.Key.ToString(CultureInfo.InvariantCulture)).Append(',').Append(entryJson, 1, entryJson.Length - 1);
            }

            return builder.Append("],\"last\":").Append(last.ToString(CultureInfo.InvariantCulture)).Append('}').ToString();
        }

        private string State(JsonData json)
        {
            if (!OnlyProperties(json, out var unknown, "set")) return unknown;
            if (_target.Mock.StateScope == StateScope.Connection) return Error(ConnectionStateError);
            if (json["set"].Exists)
            {
                var name = json["set"].AsString();
                if (string.IsNullOrEmpty(name)) return Error("The property \"set\" must be a non-empty string.");
                _target.Mock.State = name;
            }

            return "{\"ok\":true,\"state\":" + Quote(_target.Mock.State) + "}";
        }

        private static bool OnlyProperties(JsonData json, out string error, params string[] allowed)
        {
            foreach (var name in json.Properties.Keys)
            {
                if (name == "command" || Array.IndexOf(allowed, name) >= 0) continue;
                error = Error($"Unknown property \"{name}\" for this command.");
                return false;
            }

            error = null;
            return true;
        }

        private static string Error(string message) => "{\"ok\":false,\"error\":" + Quote(message) + "}";

        private static string Quote(string text) => JsonSerializer.Serialize(text, QuoteOptions);
    }
}
