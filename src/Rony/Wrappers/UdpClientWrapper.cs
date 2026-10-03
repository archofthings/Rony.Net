using System.Net;
using System.Net.Sockets;

namespace Rony.Wrappers
{
    /// <summary>
    /// Wrapper around UdpClient that exposes the Active property
    /// </summary>
    public class UdpClientWrapper : UdpClient
    {
        public UdpClientWrapper(IPEndPoint localEp) : base(localEp)
        {
        }

        /// <summary>Binds to the given endpoint; with <paramref name="dualMode"/> and an IPv6 address the socket also receives IPv4 datagrams.</summary>
        public UdpClientWrapper(IPEndPoint localEp, bool dualMode) : base((localEp ?? throw new System.ArgumentNullException(nameof(localEp))).AddressFamily)
        {
            try
            {
                if (dualMode) Client.DualMode = true;
                Client.Bind(localEp);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public UdpClientWrapper(int port) : base(port)
        {
        }

        public UdpClientWrapper(string hostName, int port) : base(hostName, port)
        {
        }
        public new bool Active => base.Active;
    }
}
