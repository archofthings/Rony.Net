using System;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Interfaces
{
    /// <summary>
    /// A listener that can simulate network failures. Implement it on a custom <see cref="IConnectionListener"/> to get
    /// <c>ResetConnection()</c>, <c>Truncated(...)</c>, <c>Corrupted(...)</c>, <c>InChunks(...)</c>, <c>Throttled(...)</c>, <see cref="Rony.Models.ClientConnection.ResetAsync"/>
    /// and <see cref="Rony.Net.MockServer.RefuseConnections"/>. A listener without it still works, without those features.
    /// </summary>
    public interface IFaultInjectionListener : IConnectionListener
    {
        /// <summary>Aborts the connection of a sender so the client sees a connection reset (RST for TCP) instead of a clean end of stream. Raises the closed event once.</summary>
        Task ResetAsync(object sender);

        /// <summary>The bytes a response is sent as: <paramref name="message"/> with the listener's framing applied.</summary>
        byte[] Frame(byte[] message);

        /// <summary>
        /// Writes bytes as they are, without framing. Unlike a reply it does not end a request: after a raw send the server
        /// finishes the request with <c>ReplyAsync</c> and an empty response, for which the listener must write nothing.
        /// </summary>
        Task SendRawAsync(byte[] data, object sender);

        /// <summary>
        /// Writes bytes as they are, in pieces of <paramref name="chunkSize"/> bytes (the last may be shorter), waiting
        /// <paramref name="delay"/> between pieces. Each piece is flushed on its own, and nothing else is written to the
        /// connection until the last one is. The waits end with <paramref name="cancellationToken"/>, which then throws
        /// <see cref="System.OperationCanceledException"/>. Unlike a reply it does not end a request: after a raw send the
        /// server finishes the request with <c>ReplyAsync</c> and an empty response, for which the listener must write nothing.
        /// </summary>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="chunkSize"/> is zero or negative, or <paramref name="delay"/> is negative.</exception>
        Task SendRawAsync(byte[] data, object sender, int chunkSize, TimeSpan delay, CancellationToken cancellationToken);

        /// <summary>Stops accepting new connections (clients get "connection refused") while open connections keep working.</summary>
        void RefuseConnections();

        /// <summary>Accepts new connections again, on the same port, after <see cref="RefuseConnections"/>.</summary>
        /// <exception cref="System.Net.Sockets.SocketException">The port could not be bound again.</exception>
        void AcceptConnections();
    }
}
