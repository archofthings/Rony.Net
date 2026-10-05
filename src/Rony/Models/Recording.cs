using Rony.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Rony.Models
{
    /// <summary>Which side of a recorded connection sent a message.</summary>
    public enum RecordedSource
    {
        /// <summary>The client that connected to the proxy.</summary>
        Client,

        /// <summary>The real server behind the proxy.</summary>
        Server
    }

    /// <summary>One message of a <see cref="RecordedConnection"/>, or the close of the connection.</summary>
    public sealed class RecordedMessage
    {
        internal RecordedMessage(RecordedSource source, byte[] body, TimeSpan offset, bool isClose)
        {
            Source = source;
            _body = body ?? new byte[0];
            Offset = offset;
            IsClose = isClose;
        }

        private readonly byte[] _body;

        /// <summary>Who sent the message, or who closed the connection first for a close event.</summary>
        public RecordedSource Source { get; }

        /// <summary>The payload without framing (a copy); empty for a close event.</summary>
        public byte[] Body => (byte[])_body.Clone();

        /// <summary>The payload as UTF-8 text.</summary>
        public string BodyString => Encoding.UTF8.GetString(_body);

        /// <summary>The time since the connection was accepted.</summary>
        public TimeSpan Offset { get; }

        /// <summary>True if <see cref="Source"/> closed or reset the connection first. A close event has no body, is always the last message and there is at most one per connection.</summary>
        public bool IsClose { get; }
    }

    /// <summary>The messages of one connection that went through a <c>RecordingProxy</c>.</summary>
    public sealed class RecordedConnection
    {
        private readonly object _syncRoot = new object();
        private readonly List<RecordedMessage> _messages = new List<RecordedMessage>();

        internal RecordedConnection(int id)
        {
            Id = id;
        }

        /// <summary>The number of the connection in its recording: 1, 2, 3 and so on, in the order they were accepted.</summary>
        public int Id { get; }

        /// <summary>The messages in the order they were relayed (a snapshot).</summary>
        public IReadOnlyList<RecordedMessage> Messages
        {
            get
            {
                lock (_syncRoot)
                    return _messages.ToArray();
            }
        }

        internal void Add(RecordedMessage message)
        {
            lock (_syncRoot)
                _messages.Add(message);
        }
    }

    /// <summary>
    /// Traffic recorded by a <c>RecordingProxy</c>: the connections and the messages exchanged on them. Save it to a file
    /// and replay it with <c>MockServer.Replay</c>.
    /// </summary>
    public sealed class Recording
    {
        private const int Version = 1;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly object _syncRoot = new object();
        private readonly List<RecordedConnection> _connections = new List<RecordedConnection>();

        /// <summary>Creates an empty recording.</summary>
        public Recording()
        {
        }

        /// <summary>The connections in the order they were accepted (a snapshot).</summary>
        public IReadOnlyList<RecordedConnection> Connections
        {
            get
            {
                lock (_syncRoot)
                    return _connections.ToArray();
            }
        }

        internal RecordedConnection AddConnection()
        {
            lock (_syncRoot)
            {
                var connection = new RecordedConnection(_connections.Count + 1);
                _connections.Add(connection);
                return connection;
            }
        }

        /// <summary>The recording as indented JSON (file format version 1).</summary>
        public string ToJson()
        {
            var connections = Connections;
            var builder = new StringBuilder();
            builder.Append("{\n  \"version\": ").Append(Version).Append(",\n  \"connections\": [");
            for (var i = 0; i < connections.Count; i++)
            {
                var messages = connections[i].Messages;
                builder.Append(i == 0 ? "\n" : ",\n").Append("    {\n      \"id\": ")
                    .Append(connections[i].Id.ToString(CultureInfo.InvariantCulture)).Append(",\n      \"messages\": [");
                for (var j = 0; j < messages.Count; j++)
                {
                    builder.Append(j == 0 ? "\n" : ",\n").Append("        ");
                    AppendMessage(builder, messages[j]);
                }
                builder.Append(messages.Count == 0 ? "]\n    }" : "\n      ]\n    }");
            }
            builder.Append(connections.Count == 0 ? "]\n}" : "\n  ]\n}");
            return builder.ToString();
        }

        private static void AppendMessage(StringBuilder builder, RecordedMessage message)
        {
            builder.Append("{ \"from\": \"").Append(message.Source == RecordedSource.Client ? "client" : "server")
                .Append("\", \"at\": ").Append(((long)message.Offset.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            if (message.IsClose)
            {
                builder.Append(", \"closed\": true }");
                return;
            }

            var body = message.Body;
            if (TryGetText(body, out var text))
                builder.Append(", \"text\": ").Append(JsonParser.Quote(text)).Append(" }");
            else
                builder.Append(", \"base64\": \"").Append(Convert.ToBase64String(body)).Append("\" }");
        }

        /// <summary>The body as text if it is valid UTF-8 that round-trips exactly and has no control characters except CR, LF and tab.</summary>
        internal static bool TryGetText(byte[] body, out string text)
        {
            text = null;
            try
            {
                var decoded = StrictUtf8.GetString(body);
                if (decoded.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')) return false;
                if (!StrictUtf8.GetBytes(decoded).SequenceEqual(body)) return false;
                text = decoded;
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Reads a recording from the JSON written by <see cref="ToJson"/>. Unknown properties are ignored.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        /// <exception cref="FormatException">The text is not valid JSON, the version is not 1 or the content is malformed; the message names the problem.</exception>
        public static Recording Parse(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var root = JsonData.Parse(json);
            if (root.Kind != JsonDataKind.Object) throw new FormatException("The recording must be a JSON object.");
            if (root["version"].AsNumber() != Version)
                throw new FormatException($"Unsupported recording version {root["version"]}; only version {Version} is supported.");
            if (root["connections"].Kind != JsonDataKind.Array)
                throw new FormatException("The recording has no \"connections\" array.");

            var recording = new Recording();
            var position = 0;
            foreach (var item in root["connections"].Items)
            {
                position++;
                if (item.Kind != JsonDataKind.Object) throw new FormatException($"Connection {position} must be an object.");
                var connection = recording.AddConnection();
                var name = $"connection {connection.Id}";
                if (item["messages"].Kind != JsonDataKind.Array) throw new FormatException($"The {name} has no \"messages\" array.");

                var index = 0;
                var closed = false;
                foreach (var entry in item["messages"].Items)
                {
                    index++;
                    var where = $"The {name}, message {index}";
                    if (closed) throw new FormatException($"{where} comes after the close event; a close must be the last message.");
                    var message = ParseMessage(entry, where);
                    closed = message.IsClose;
                    connection.Add(message);
                }
            }
            return recording;
        }

        private static RecordedMessage ParseMessage(JsonData entry, string where)
        {
            if (entry.Kind != JsonDataKind.Object) throw new FormatException($"{where} must be an object.");

            var from = entry["from"].AsString();
            if (from != "client" && from != "server")
                throw new FormatException($"{where} needs \"from\": \"client\" or \"server\".");
            var source = from == "client" ? RecordedSource.Client : RecordedSource.Server;

            var offset = TimeSpan.Zero;
            if (entry["at"].Exists)
            {
                var at = entry["at"].AsNumber();
                if (at == null || at < 0 || at > int.MaxValue) throw new FormatException($"{where}: \"at\" must be a number of milliseconds, zero or more.");
                offset = TimeSpan.FromMilliseconds(Math.Floor(at.Value));
            }

            var hasText = entry["text"].Exists;
            var hasBase64 = entry["base64"].Exists;
            if (entry["closed"].Exists && entry["closed"].AsBoolean() == null)
                throw new FormatException($"{where}: \"closed\" must be true or false.");
            if (entry["closed"].AsBoolean() == true)
            {
                if (hasText || hasBase64) throw new FormatException($"{where} is a close event and cannot have \"text\" or \"base64\".");
                return new RecordedMessage(source, null, offset, true);
            }

            if (hasText == hasBase64) throw new FormatException($"{where} needs exactly one of \"text\" or \"base64\".");
            if (hasText)
            {
                var text = entry["text"].AsString();
                if (text == null) throw new FormatException($"{where}: \"text\" must be a string.");
                return new RecordedMessage(source, Encoding.UTF8.GetBytes(text), offset, false);
            }

            var base64 = entry["base64"].AsString();
            try
            {
                return new RecordedMessage(source, Convert.FromBase64String(base64 ?? throw new FormatException()), offset, false);
            }
            catch (FormatException)
            {
                throw new FormatException($"{where}: \"base64\" must be a base64 string.");
            }
        }

        /// <summary>Writes the recording to <paramref name="path"/> as indented UTF-8 JSON without a byte order mark, replacing the file.</summary>
        public void Save(string path)
        {
            File.WriteAllText(path, ToJson(), new UTF8Encoding(false));
        }

        /// <summary>Reads a recording from a file written by <see cref="Save"/>.</summary>
        /// <exception cref="FormatException">The file content is malformed; see <see cref="Parse"/>.</exception>
        public static Recording Load(string path)
        {
            return Parse(File.ReadAllText(path));
        }
    }
}
