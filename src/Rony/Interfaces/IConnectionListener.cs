using System;
using System.Net;
using System.Threading.Tasks;

namespace Rony.Interfaces
{
    /// <summary>
    /// A listener with connections, such as TCP. Implement it on a custom listener to get
    /// <see cref="Rony.Net.MockServer.Connections"/>, greetings (<c>OnConnect()</c>), pushed messages and connection
    /// logging. A listener that only implements <see cref="IListener"/> still works, without those features.
    /// </summary>
    public interface IConnectionListener : IListener
    {
        /// <summary>
        /// A client connected. Raised with the connection's sender handle (the same object later used as
        /// <see cref="Rony.Models.Message.Sender"/>) and the client's address, before any of its requests are received.
        /// </summary>
        event Action<object, EndPoint> ConnectionOpened;

        /// <summary>A connection was closed, by either side. Raised once per connection, with its sender handle.</summary>
        event Action<object> ConnectionClosed;

        /// <summary>
        /// A connection failed: a TLS handshake failed, or the connection broke while reading.
        /// Raised with the client's address (when known) and the error.
        /// </summary>
        event Action<EndPoint, Exception> ConnectionFailed;

        /// <summary>Sends a message the client didn't ask for, framed like a response, without ending any request.</summary>
        Task SendAsync(byte[] data, object sender);

        /// <summary>Marks a request as handled when it gets no reply, so a connection the client closed can be released.</summary>
        void CompleteWithoutReply(object sender);
    }
}
