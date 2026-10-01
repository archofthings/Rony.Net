using Rony.Models;
using System;
using System.Net;
using System.Threading.Tasks;

namespace Rony.Interfaces
{
    /// <summary>
    /// The transport a <see cref="Rony.Net.MockServer"/> runs on. Implement it to plug in your own transport.
    /// </summary>
    public interface IListener : IDisposable
    {
        /// <summary>The address the listener binds to.</summary>
        IPAddress Address { get; }

        /// <summary>
        /// The port the server listens on. When the server was created with port 0,
        /// this is the port the operating system assigned once the server has started.
        /// </summary>
        int Port { get; }

        /// <summary>Whether the listener is started.</summary>
        bool Active { get; }

        /// <summary>Waits for the next request. Throws <see cref="ObjectDisposedException"/> once stopped.</summary>
        Task<Message> ReceiveAsync();
        /// <summary>Sends a text response to the sender of a request.</summary>
        Task ReplyAsync(string response, object sender);
        /// <summary>Sends a response to the sender of a request (<see cref="Message.Sender"/>).</summary>
        Task ReplyAsync(byte[] response, object sender);

        /// <summary>
        /// Ends the conversation with a sender: closes a TCP connection, does nothing for UDP.
        /// </summary>
        Task CloseAsync(object sender);

        /// <summary>Stops listening.</summary>
        void Stop();
        /// <summary>Starts listening.</summary>
        void Start();
    }
}
