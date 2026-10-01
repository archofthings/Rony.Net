using Rony.Helpers;
using System;
using System.Net;

namespace Rony.Models
{
    /// <summary>
    /// A request the mock server received.
    /// </summary>
    public sealed class ReceivedRequest
    {
        public ReceivedRequest(byte[] body, EndPoint remoteEndPoint, DateTimeOffset timestamp, bool matched)
        {
            Body = body ?? new byte[0];
            RemoteEndPoint = remoteEndPoint;
            Timestamp = timestamp;
            Matched = matched;
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

        public override string ToString()
        {
            return $"{ByteFormatter.Describe(Body)}{(Matched ? string.Empty : " (unmatched)")}";
        }
    }
}
