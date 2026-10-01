using System;
using System.Collections.Generic;

namespace Rony.Interfaces
{
    /// <summary>
    /// Splits a TCP byte stream into messages, and prepares responses for sending.
    /// </summary>
    public interface IMessageFraming
    {
        /// <summary>
        /// Extracts every complete message at the start of <paramref name="data"/>.
        /// </summary>
        /// <param name="data">Bytes received so far that are not part of an extracted message yet.</param>
        /// <param name="endOfBurst">True when no more data is immediately available on the connection.</param>
        /// <param name="consumed">How many bytes of <paramref name="data"/> the extracted messages used.</param>
        IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed);

        /// <summary>
        /// Converts a configured response into the bytes sent on the wire.
        /// </summary>
        byte[] Encode(byte[] response);
    }
}
