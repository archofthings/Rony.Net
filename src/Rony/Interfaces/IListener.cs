using Rony.Models;
using System;
using System.Net;
using System.Threading.Tasks;

namespace Rony.Interfaces
{
    public interface IListener : IDisposable
    {
        IPAddress Address { get; }

        /// <summary>
        /// The port the server listens on. When the server was created with port 0,
        /// this is the port the operating system assigned once the server has started.
        /// </summary>
        int Port { get; }

        bool Active { get; }

        Task<Message> ReceiveAsync();
        Task ReplyAsync(string response, object sender);
        Task ReplyAsync(byte[] response, object sender);

        /// <summary>
        /// Ends the conversation with a sender: closes a TCP connection, does nothing for UDP.
        /// </summary>
        Task CloseAsync(object sender);

        void Stop();
        void Start();
    }
}
