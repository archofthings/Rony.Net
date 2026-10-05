using Rony.Helpers;
using System;
using System.Globalization;
using System.Net;
using System.Text;

namespace Rony.Models
{
    /// <summary>
    /// A request the mock server received.
    /// </summary>
    public sealed class ReceivedRequest
    {
        public ReceivedRequest(byte[] body, EndPoint remoteEndPoint, DateTimeOffset timestamp, bool matched)
            : this(body, remoteEndPoint, timestamp, matched, null)
        {
        }

        public ReceivedRequest(byte[] body, EndPoint remoteEndPoint, DateTimeOffset timestamp, bool matched, int? connectionId)
        {
            Body = body ?? new byte[0];
            RemoteEndPoint = remoteEndPoint;
            Timestamp = timestamp;
            Matched = matched;
            ConnectionId = connectionId;
        }

        /// <summary>The request bytes, after framing was removed.</summary>
        public byte[] Body { get; }

        /// <summary>The request decoded as UTF-8 text.</summary>
        public string BodyString => Body.GetString();

        /// <summary>The client's address, when known.</summary>
        public EndPoint RemoteEndPoint { get; }

        /// <summary>When the server received the request.</summary>
        public DateTimeOffset Timestamp { get; }

        /// <summary>Whether a configured response (including the "any request" one) handled this request.</summary>
        public bool Matched { get; }

        /// <summary>
        /// The <see cref="ClientConnection.Id"/> of the TCP connection the request arrived on; null for UDP,
        /// and for requests passed to <c>Match(...)</c> directly.
        /// </summary>
        public int? ConnectionId { get; }

        /// <summary>
        /// The request as one line of JSON (no line breaks): <c>time</c>, <c>connection</c> (omitted when there is no connection id),
        /// <c>remote</c> (omitted when unknown), <c>matched</c> and the body as <c>text</c> (valid UTF-8 without control
        /// characters other than CR, LF and tab) or else <c>base64</c>.
        /// </summary>
        public string ToJson()
        {
            var builder = new StringBuilder();
            builder.Append("{\"time\":\"").Append(Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)).Append('"');
            if (ConnectionId.HasValue)
                builder.Append(",\"connection\":").Append(ConnectionId.Value.ToString(CultureInfo.InvariantCulture));
            var remote = RemoteEndPoint?.ToString();
            if (!string.IsNullOrEmpty(remote))
                builder.Append(",\"remote\":").Append(JsonParser.Quote(remote));
            builder.Append(",\"matched\":").Append(Matched ? "true" : "false");
            if (Recording.TryGetText(Body, out var text))
                builder.Append(",\"text\":").Append(JsonParser.Quote(text));
            else
                builder.Append(",\"base64\":\"").Append(Convert.ToBase64String(Body)).Append('"');
            return builder.Append('}').ToString();
        }

        public override string ToString()
        {
            return $"{ByteFormatter.Describe(Body)}{(Matched ? string.Empty : " (unmatched)")}{(ConnectionId.HasValue ? $" on connection #{ConnectionId}" : string.Empty)}";
        }
    }
}
