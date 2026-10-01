using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    /// <summary>
    /// A plain TCP server. Connections stay open across requests unless <see cref="TcpServerBase.KeepAlive"/> is false.
    /// </summary>
    public class TcpServer : TcpServerBase
    {
        /// <param name="address">Address to listen on.</param>
        /// <param name="port">Port to listen on. Use 0 to let the operating system pick a free port; read it from <see cref="TcpServerBase.Port"/> after starting.</param>
        public TcpServer(IPAddress address, int port = 3000) : base(address, port)
        {
        }

        /// <summary>Listens on 127.0.0.1. Use port 0 to let the operating system pick a free port.</summary>
        public TcpServer(int port = 3000) : this(IPAddress.Loopback, port)
        {
        }

        /// <summary>Listens on the given IP address, for example "0.0.0.0" for all interfaces.</summary>
        public TcpServer(string address, int port = 3000) : this(IPAddress.Parse(address), port)
        {
        }

        /// <inheritdoc />
        protected override Task<Stream> OpenStreamAsync(TcpClient client)
        {
            return Task.FromResult<Stream>(client.GetStream());
        }
    }
}
