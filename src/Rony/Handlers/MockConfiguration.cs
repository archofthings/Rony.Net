using Rony.Interfaces;
using Rony.Listeners;
using Rony.Models;
using Rony.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Rony.Handlers
{
    /// <summary>Builds a <see cref="MockServer"/> (listener and rules) from a configuration in JSON (file format version 1).</summary>
    internal static class MockConfiguration
    {
        private const string Root = "configuration";
        private const int Version = 1;
        // SslProtocols.Tls13 does not exist in .NET Standard 2.1; the value is the same.
        private const SslProtocols Tls13 = (SslProtocols)12288;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        private static readonly string[] ResponseFields = { "reply", "noReply", "disconnect", "reset", "afterMs", "goTo" };
        private static readonly string[] MatcherFields = { "request", "match", "json" };

        private sealed class Body
        {
            public Body(byte[] bytes, string text)
            {
                Bytes = bytes;
                Text = text;
            }

            public byte[] Bytes { get; }

            /// <summary>The text of a body written as a JSON string or <c>text</c>; null for <c>base64</c>.</summary>
            public string Text { get; }
        }

        private sealed class Response
        {
            public Body Reply;
            public bool NoReply;
            public bool Disconnect;
            public bool Reset;
            public int? DelayMs;
            public string GoTo;
        }

        private sealed class Rule
        {
            public string Where;
            public Body Request;
            public Regex Pattern;
            public JsonData Json;
            public string State;
            public List<Response> Responses;
        }

        private sealed class ServerSettings
        {
            public string Transport = "tcp";
            public IPAddress Address = IPAddress.Loopback;
            public int Port;
            public bool DualMode;
            public string Path;
            public bool KeepAlive = true;
            public IMessageFraming Framing = MessageFraming.None;
            public string CertificatePath;
            public string CertificatePassword;
            public SslProtocols Protocol = SslProtocols.None;
            public bool RequireClientCertificate;
            public int MaxBufferedBytes = DefaultMaxBufferedBytes;
        }

        private const int DefaultMaxBufferedBytes = 16 * 1024 * 1024;
        private const int MaxCertificateBytes = 1024 * 1024;

        public static MockServer Create(string json, string baseDirectory)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));

            var root = JsonData.Parse(json);
            if (root.Kind != JsonDataKind.Object) throw Error(Root, "must be a JSON object");
            CheckProperties(root, Root, "version", "server", "stateScope", "failOnUnmatched", "onConnect", "onUnmatched", "rules");

            if (!root["version"].Exists) throw Error("version", $"is missing; the only supported version is {Version}");
            if (root["version"].AsNumber() != Version)
                throw Error("version", $"{root["version"]} is not supported; only version {Version} is supported");

            var settings = ParseServer(root["server"], baseDirectory);
            var stateScope = OptString(root, "stateScope", Root);
            if (stateScope != null && stateScope != "server" && stateScope != "connection")
                throw Error("stateScope", $"\"{stateScope}\" is not valid; use \"server\" or \"connection\"");
            var failOnUnmatched = OptBool(root, "failOnUnmatched", Root) ?? false;

            var onConnect = ParseOptionalResponses(root, "onConnect");
            if (onConnect != null && settings.Transport == "udp") throw Error("onConnect", "not allowed with transport \"udp\"");
            var onUnmatched = ParseOptionalResponses(root, "onUnmatched");
            var rules = ParseRules(root["rules"]);

            var server = CreateServer(settings);
            try
            {
                var mock = server.Mock;
                if (stateScope == "connection") mock.StateScope = StateScope.Connection;
                mock.FailOnUnmatched = failOnUnmatched;
                if (onConnect != null) Apply(() => AddResponses(mock.OnConnect(), onConnect, null), "onConnect");
                if (onUnmatched != null) Apply(() => AddResponses(mock.OnUnmatched(), onUnmatched, null), "onUnmatched");
                foreach (var rule in rules)
                    Apply(() => AddRule(mock, rule), rule.Where);
                return server;
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        #region Parsing

        private static ServerSettings ParseServer(JsonData server, string baseDirectory)
        {
            var settings = new ServerSettings();
            if (!server.Exists) return settings;
            if (server.Kind != JsonDataKind.Object) throw Error("server", "must be an object");
            CheckProperties(server, "server", "transport", "address", "port", "dualMode", "path", "keepAlive", "framing", "maxBufferedBytes", "tls");

            settings.Transport = OptString(server, "transport", "server") ?? "tcp";
            string[] allowed;
            switch (settings.Transport)
            {
                case "tcp":
                    allowed = new[] { "address", "port", "dualMode", "keepAlive", "framing", "maxBufferedBytes" };
                    break;
                case "tls":
                    allowed = new[] { "address", "port", "dualMode", "keepAlive", "framing", "maxBufferedBytes", "tls" };
                    break;
                case "udp":
                    allowed = new[] { "address", "port", "dualMode" };
                    break;
                case "unix":
                    allowed = new[] { "path", "keepAlive", "framing", "maxBufferedBytes" };
                    break;
                default:
                    throw Error("server.transport", $"\"{settings.Transport}\" is not valid; use \"tcp\", \"tls\", \"udp\" or \"unix\"");
            }

            foreach (var name in server.Properties.Keys)
            {
                if (name != "transport" && Array.IndexOf(allowed, name) < 0)
                    throw Error("server." + name, $"not allowed with transport \"{settings.Transport}\"");
            }

            if (server["address"].Exists)
            {
                var address = OptString(server, "address", "server");
                if (!IPAddress.TryParse(address, out var parsed)) throw Error("server.address", $"\"{address}\" is not an IP address");
                settings.Address = parsed;
            }

            settings.Port = OptInt(server, "port", "server", 0, 65535) ?? 0;
            settings.DualMode = OptBool(server, "dualMode", "server") ?? false;
            if (settings.DualMode && settings.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                throw Error("server.dualMode", "needs an IPv6 address in \"server.address\", for example \"::\"");

            settings.Path = OptString(server, "path", "server");
            if (settings.Path != null && settings.Path.Length == 0) throw Error("server.path", "must not be empty");
            settings.KeepAlive = OptBool(server, "keepAlive", "server") ?? true;
            settings.MaxBufferedBytes = OptInt(server, "maxBufferedBytes", "server", 0, int.MaxValue) ?? DefaultMaxBufferedBytes;
            if (server["framing"].Exists) settings.Framing = ParseFraming(server["framing"], "server.framing");
            if (settings.Transport == "tls") ParseTls(server["tls"], settings, baseDirectory);
            return settings;
        }

        private static void ParseTls(JsonData tls, ServerSettings settings, string baseDirectory)
        {
            if (!tls.Exists) return;
            if (tls.Kind != JsonDataKind.Object) throw Error("server.tls", "must be an object");
            CheckProperties(tls, "server.tls", "certificate", "password", "protocol", "requireClientCertificate");

            var certificate = OptString(tls, "certificate", "server.tls");
            var password = OptString(tls, "password", "server.tls");
            if (certificate == null && password != null) throw Error("server.tls.password", "needs \"certificate\"");
            if (certificate != null)
            {
                var path = Path.IsPathRooted(certificate)
                    ? certificate
                    : Path.Combine(baseDirectory ?? Directory.GetCurrentDirectory(), certificate);
                path = Path.GetFullPath(path);
                if (!File.Exists(path)) throw Error("server.tls.certificate", $"file not found: {path}");
                settings.CertificatePath = path;
                settings.CertificatePassword = password;
            }

            var protocol = OptString(tls, "protocol", "server.tls") ?? "none";
            switch (protocol)
            {
                case "none":
                    settings.Protocol = SslProtocols.None;
                    break;
                case "tls12":
                    settings.Protocol = SslProtocols.Tls12;
                    break;
                case "tls13":
                    settings.Protocol = Tls13;
                    break;
                default:
                    throw Error("server.tls.protocol", $"\"{protocol}\" is not valid; use \"none\", \"tls12\" or \"tls13\"");
            }

            settings.RequireClientCertificate = OptBool(tls, "requireClientCertificate", "server.tls") ?? false;
        }

        private static IMessageFraming ParseFraming(JsonData framing, string where)
        {
            try
            {
                return ParseFramingCore(framing, where);
            }
            catch (ArgumentException exception)
            {
                throw Error(where, exception.Message);
            }
        }

        private static IMessageFraming ParseFramingCore(JsonData framing, string where)
        {
            if (framing.Kind != JsonDataKind.Object) throw Error(where, "must be an object");
            var type = OptString(framing, "type", where);
            if (type == null) throw Error(where, "\"type\" is required");

            switch (type)
            {
                case "none":
                    CheckProperties(framing, where, "type");
                    return MessageFraming.None;
                case "delimiter":
                    CheckProperties(framing, where, "type", "delimiter");
                    if (!framing["delimiter"].Exists) throw Error(where, "\"delimiter\" is required");
                    var delimiter = ParseBody(framing["delimiter"], Location(where, "delimiter")).Bytes;
                    if (delimiter.Length == 0) throw Error(Location(where, "delimiter"), "must not be empty");
                    return MessageFraming.Delimiter(delimiter);
                case "lengthPrefix":
                    CheckProperties(framing, where, "type", "prefixLength", "bigEndian", "includesPrefix");
                    var prefixLength = OptInt(framing, "prefixLength", where, 1, 4) ?? 4;
                    if (prefixLength == 3) throw Error(Location(where, "prefixLength"), "must be 1, 2 or 4");
                    return MessageFraming.LengthPrefix(prefixLength, OptBool(framing, "bigEndian", where) ?? true, OptBool(framing, "includesPrefix", where) ?? false);
                case "fixedLength":
                    CheckProperties(framing, where, "type", "length", "padding");
                    var length = RequiredInt(framing, "length", where, 1, int.MaxValue);
                    return MessageFraming.FixedLength(length, (byte)(OptInt(framing, "padding", where, 0, 255) ?? 0));
                case "startEnd":
                    CheckProperties(framing, where, "type", "start", "end");
                    return MessageFraming.StartEnd((byte)RequiredInt(framing, "start", where, 0, 255), (byte)RequiredInt(framing, "end", where, 0, 255));
                case "stxEtx":
                    CheckProperties(framing, where, "type");
                    return MessageFraming.StxEtx;
                default:
                    throw Error(Location(where, "type"), $"\"{type}\" is not valid; use \"none\", \"delimiter\", \"lengthPrefix\", \"fixedLength\", \"startEnd\" or \"stxEtx\"");
            }
        }

        private static List<Response> ParseOptionalResponses(JsonData root, string name)
        {
            var value = root[name];
            if (!value.Exists) return null;
            if (value.Kind != JsonDataKind.Object) throw Error(name, "must be an object");
            return ParseResponses(value, name);
        }

        private static List<Rule> ParseRules(JsonData rules)
        {
            var result = new List<Rule>();
            if (!rules.Exists) return result;
            if (rules.Kind != JsonDataKind.Array) throw Error("rules", "must be an array");

            for (var i = 0; i < rules.Items.Count; i++)
            {
                var where = $"rules[{i}]";
                var item = rules.Items[i];
                if (item.Kind != JsonDataKind.Object) throw Error(where, "must be an object");

                var rule = new Rule { Where = where };
                CheckProperties(item, where, ResponseFields.Concat(new[] { "request", "match", "json", "state", "replies" }).ToArray());
                var matchers = MatcherFields.Where(field => item[field].Exists).ToArray();
                if (matchers.Length == 0) throw Error(where, "needs one of \"request\", \"match\" or \"json\"");
                if (matchers.Length > 1) throw Error(where, $"\"{matchers[0]}\" and \"{matchers[1]}\" cannot both be set; use one of \"request\", \"match\" or \"json\"");
                rule.Responses = ParseResponses(item, where, "request", "match", "json", "state");

                switch (matchers[0])
                {
                    case "request":
                        rule.Request = ParseBody(item["request"], Location(where, "request"));
                        break;
                    case "match":
                        var pattern = OptString(item, "match", where);
                        if (pattern == null) throw Error(Location(where, "match"), "must be a string");
                        try
                        {
                            rule.Pattern = new Regex(pattern, RegexOptions.None, RegexTimeout);
                        }
                        catch (ArgumentException exception)
                        {
                            throw Error(Location(where, "match"), "is not a valid regular expression: " + exception.Message);
                        }
                        break;
                    default:
                        rule.Json = item["json"];
                        break;
                }

                rule.State = OptString(item, "state", where);
                if (rule.State != null && rule.State.Length == 0) throw Error(Location(where, "state"), "must not be empty");
                result.Add(rule);
            }

            return result;
        }

        /// <summary>Reads the response fields of <paramref name="holder"/>, or its <c>replies</c> array.</summary>
        private static List<Response> ParseResponses(JsonData holder, string where, params string[] otherProperties)
        {
            CheckProperties(holder, where, ResponseFields.Concat(otherProperties).Concat(new[] { "replies" }).ToArray());
            if (!holder["replies"].Exists)
                return new List<Response> { ParseResponse(holder, where) };

            foreach (var field in ResponseFields)
            {
                if (holder[field].Exists) throw Error(where, $"\"{field}\" and \"replies\" cannot both be set");
            }

            var replies = holder["replies"];
            if (replies.Kind != JsonDataKind.Array || replies.Items.Count == 0)
                throw Error(Location(where, "replies"), "must be a non-empty array");

            var result = new List<Response>();
            for (var i = 0; i < replies.Items.Count; i++)
            {
                var itemWhere = $"{Location(where, "replies")}[{i}]";
                var item = replies.Items[i];
                if (item.Kind != JsonDataKind.Object) throw Error(itemWhere, "must be an object");
                CheckProperties(item, itemWhere, ResponseFields);
                result.Add(ParseResponse(item, itemWhere));
            }
            return result;
        }

        private static Response ParseResponse(JsonData value, string where)
        {
            var response = new Response();
            if (value["reply"].Exists) response.Reply = ParseBody(value["reply"], Location(where, "reply"));
            response.NoReply = OptBool(value, "noReply", where) ?? false;
            response.Disconnect = OptBool(value, "disconnect", where) ?? false;
            response.Reset = OptBool(value, "reset", where) ?? false;
            response.DelayMs = OptInt(value, "afterMs", where, 0, int.MaxValue);
            response.GoTo = OptString(value, "goTo", where);
            if (response.GoTo != null && response.GoTo.Length == 0) throw Error(Location(where, "goTo"), "must not be empty");

            if (response.Reply != null && response.NoReply) throw Error(where, "\"reply\" and \"noReply\" cannot both be set");
            if (response.Disconnect && response.Reset) throw Error(where, "\"disconnect\" and \"reset\" cannot both be set");
            if (response.NoReply && response.Disconnect) throw Error(where, "\"noReply\" and \"disconnect\" cannot both be set");
            if (response.NoReply && response.Reset) throw Error(where, "\"noReply\" and \"reset\" cannot both be set");
            if (response.Reply == null && !response.NoReply && !response.Disconnect && !response.Reset)
                throw Error(where, "needs a response: \"reply\", \"noReply\", \"disconnect\" or \"reset\"");
            return response;
        }

        private static Body ParseBody(JsonData value, string where)
        {
            if (value.Kind == JsonDataKind.String)
            {
                var text = value.AsString();
                return new Body(Encoding.UTF8.GetBytes(text), text);
            }

            if (value.Kind != JsonDataKind.Object)
                throw Error(where, "must be a string, or an object with \"text\" or \"base64\"");
            CheckProperties(value, where, "text", "base64");
            if (value["text"].Exists == value["base64"].Exists)
                throw Error(where, "needs exactly one of \"text\" or \"base64\"");

            if (value["text"].Exists)
            {
                var text = value["text"].AsString();
                if (text == null) throw Error(Location(where, "text"), "must be a string");
                return new Body(Encoding.UTF8.GetBytes(text), text);
            }

            var base64 = value["base64"].AsString();
            if (base64 == null) throw Error(Location(where, "base64"), "must be a string");
            try
            {
                return new Body(Convert.FromBase64String(base64), null);
            }
            catch (FormatException)
            {
                throw Error(Location(where, "base64"), "is not valid base64");
            }
        }

        private static void CheckProperties(JsonData value, string where, params string[] known)
        {
            foreach (var name in value.Properties.Keys)
            {
                if (Array.IndexOf(known, name) < 0) throw Error(where, $"unknown property \"{name}\"");
            }
        }

        private static string OptString(JsonData value, string name, string where)
        {
            var property = value[name];
            if (!property.Exists) return null;
            return property.AsString() ?? throw Error(Location(where, name), "must be a string");
        }

        private static bool? OptBool(JsonData value, string name, string where)
        {
            var property = value[name];
            if (!property.Exists) return null;
            return property.AsBoolean() ?? throw Error(Location(where, name), "must be true or false");
        }

        private static int? OptInt(JsonData value, string name, string where, int min, int max)
        {
            var property = value[name];
            if (!property.Exists) return null;
            var number = property.AsNumber();
            if (number == null || number != Math.Floor(number.Value) || number < min || number > max)
                throw Error(Location(where, name), $"must be a whole number from {min} to {max}");
            return (int)number.Value;
        }

        private static int RequiredInt(JsonData value, string name, string where, int min, int max)
        {
            return OptInt(value, name, where, min, max) ?? throw Error(where, $"\"{name}\" is required");
        }

        private static string Location(string where, string name) => where == Root ? name : where + "." + name;

        private static FormatException Error(string where, string message) => new FormatException($"{where}: {message}");

        #endregion

        #region Building

        private static MockServer CreateServer(ServerSettings settings)
        {
            X509Certificate2 certificate = null;
            try
            {
                IListener listener;
                switch (settings.Transport)
                {
                    case "udp":
                        listener = new UdpServer(new IPEndPoint(settings.Address, settings.Port), settings.DualMode);
                        break;
                    case "unix":
                        listener = settings.Path == null
                            ? new UnixSocketServer { KeepAlive = settings.KeepAlive, Framing = settings.Framing, MaxBufferedBytes = settings.MaxBufferedBytes }
                            : new UnixSocketServer(settings.Path) { KeepAlive = settings.KeepAlive, Framing = settings.Framing, MaxBufferedBytes = settings.MaxBufferedBytes };
                        break;
                    case "tls":
                        certificate = settings.CertificatePath == null
                            ? TestCertificate.CreateSelfSigned()
                            : LoadCertificate(settings.CertificatePath, settings.CertificatePassword);
                        listener = new TcpServerSsl(settings.Address, settings.Port, certificate, settings.Protocol)
                        {
                            DualMode = settings.DualMode,
                            KeepAlive = settings.KeepAlive,
                            Framing = settings.Framing,
                            MaxBufferedBytes = settings.MaxBufferedBytes,
                            RequireClientCertificate = settings.RequireClientCertificate
                        };
                        break;
                    default:
                        listener = new TcpServer(settings.Address, settings.Port)
                        {
                            DualMode = settings.DualMode,
                            KeepAlive = settings.KeepAlive,
                            Framing = settings.Framing,
                            MaxBufferedBytes = settings.MaxBufferedBytes
                        };
                        break;
                }

                MockServer server;
                try
                {
                    server = new MockServer(listener);
                }
                catch
                {
                    listener.Dispose();
                    throw;
                }

                if (certificate != null) server.Own(certificate);
                return server;
            }
            catch
            {
                certificate?.Dispose();
                throw;
            }
        }

        private static X509Certificate2 LoadCertificate(string path, string password)
        {
            X509Certificate2 certificate;
            try
            {
                certificate = new X509Certificate2(ReadCertificateFile(path), password);
            }
            catch (Exception exception) when (exception is CryptographicException || exception is IOException || exception is UnauthorizedAccessException)
            {
                throw Error("server.tls.certificate", $"cannot load the certificate {Path.GetFullPath(path)}: {exception.Message}");
            }

            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw Error("server.tls.certificate", $"the certificate {path} has no private key");
            }
            return certificate;
        }

        /// <summary>Reads a certificate file that is a regular file of at most 1 MiB; never reads more than 1 MiB + 1 byte.</summary>
        private static byte[] ReadCertificateFile(string path)
        {
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
                throw Error("server.tls.certificate", $"{full}: not a regular file");
            // A pipe, device or socket reports length 0, and opening a pipe would block until something writes to it.
            if (info.Length == 0)
                throw Error("server.tls.certificate", $"{full}: empty or not a regular file");
            if (info.Length > MaxCertificateBytes)
                throw Error("server.tls.certificate", $"{full}: larger than 1 MiB");

            // The file can grow after the check, so the read itself is bounded too.
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var content = new MemoryStream())
            {
                var buffer = new byte[8192];
                int read;
                while (content.Length <= MaxCertificateBytes && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxCertificateBytes + 1 - content.Length))) > 0)
                    content.Write(buffer, 0, read);
                if (content.Length > MaxCertificateBytes)
                    throw Error("server.tls.certificate", $"{full}: larger than 1 MiB");
                return content.ToArray();
            }
        }

        /// <summary>Runs <paramref name="action"/> and reports a rule the mock rejects (for example a duplicate request) as a format error.</summary>
        private static void Apply(Action action, string where)
        {
            try
            {
                action();
            }
            catch (ArgumentException exception)
            {
                throw Error(where, exception.Message);
            }
        }

        private static void AddRule(RequestHandler mock, Rule rule)
        {
            RequestHandler start;
            if (rule.Request != null)
                start = rule.State == null ? mock.Send(rule.Request.Bytes) : mock.InState(rule.State).Send(rule.Request.Bytes);
            else if (rule.Pattern != null)
                start = rule.State == null ? mock.Send(rule.Pattern) : mock.InState(rule.State).Send(rule.Pattern);
            else
            {
                var expected = rule.Json;
                Func<JsonData, bool> predicate = actual => Contains(actual, expected);
                start = rule.State == null ? mock.SendJson(predicate) : mock.InState(rule.State).SendJson(predicate);
            }

            AddResponses(start, rule.Responses, rule.Pattern);
        }

        /// <summary>Adds the first response through <paramref name="start"/> (which has a rule pending), later ones with <c>Then...</c>.</summary>
        private static void AddResponses(RequestHandler start, List<Response> responses, Regex pattern)
        {
            ResponseBuilder builder = null;
            foreach (var response in responses)
            {
                var first = builder == null;
                if (response.Reply != null)
                {
                    if (pattern != null && response.Reply.Text != null)
                    {
                        // $1, ${name} and $0 are replaced by the capture groups of the request.
                        var template = response.Reply.Text;
                        Func<Match, string> expand = match => match.Result(template);
                        builder = first ? start.ReceiveMatch(expand) : builder.ThenMatch(expand);
                    }
                    else
                    {
                        builder = first ? start.Receive(response.Reply.Bytes) : builder.Then(response.Reply.Bytes);
                    }

                    if (response.Disconnect) builder.AndDisconnect();
                    else if (response.Reset) builder.AndResetConnection();
                }
                else if (response.NoReply)
                {
                    builder = first ? start.NoReply() : builder.ThenNoReply();
                }
                else if (response.Disconnect)
                {
                    builder = first ? start.Disconnect() : builder.ThenDisconnect();
                }
                else
                {
                    builder = first ? start.ResetConnection() : builder.ThenResetConnection();
                }

                if (response.DelayMs != null) builder.After(TimeSpan.FromMilliseconds(response.DelayMs.Value));
                if (response.GoTo != null) builder.GoTo(response.GoTo);
            }
        }

        #endregion

        #region JSON matching

        /// <summary>
        /// Whether <paramref name="actual"/> contains everything in <paramref name="expected"/>: objects recursively (extra
        /// properties are fine), arrays and scalars must be equal.
        /// </summary>
        internal static bool Contains(JsonData actual, JsonData expected)
        {
            if (expected.Kind != JsonDataKind.Object) return Same(actual, expected);
            if (actual.Kind != JsonDataKind.Object) return false;
            foreach (var property in expected.Properties)
            {
                if (!actual.Properties.TryGetValue(property.Key, out var value) || !Contains(value, property.Value)) return false;
            }
            return true;
        }

        private static bool Same(JsonData a, JsonData b)
        {
            if (a.Kind != b.Kind) return false;
            switch (a.Kind)
            {
                case JsonDataKind.Boolean:
                    return a.AsBoolean() == b.AsBoolean();
                case JsonDataKind.Number:
                    return a.AsNumber() == b.AsNumber();
                case JsonDataKind.String:
                    return a.AsString() == b.AsString();
                case JsonDataKind.Array:
                    return a.Items.Count == b.Items.Count && a.Items.Zip(b.Items, Same).All(same => same);
                case JsonDataKind.Object:
                    return a.Count == b.Count && b.Properties.All(property => a.Properties.TryGetValue(property.Key, out var value) && Same(value, property.Value));
                default:
                    return true;
            }
        }

        #endregion
    }
}
