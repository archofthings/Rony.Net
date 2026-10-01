using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    public class TcpServer : TcpServerBase
    {
        /// <param name="address">Address to listen on.</param>
        /// <param name="port">Port to listen on. Use 0 to let the operating system pick a free port; read it from <see cref="TcpServerBase.Port"/> after starting.</param>
        public TcpServer(IPAddress address, int port = 3000) : base(address, port)
        {
        }

        public TcpServer(int port = 3000) : this(IPAddress.Loopback, port)
        {
        }

        public TcpServer(string address, int port = 3000) : this(IPAddress.Parse(address), port)
        {
        }

        protected override Task<Stream> OpenStreamAsync(TcpClient client)
        {
            return Task.FromResult<Stream>(client.GetStream());
        }
    }
}
