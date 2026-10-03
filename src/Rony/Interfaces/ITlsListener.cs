using Rony.Models;

namespace Rony.Interfaces
{
    /// <summary>
    /// A connection listener which knows the TLS details of its connections. Implement it on a custom
    /// <see cref="IConnectionListener"/> to fill <see cref="ClientConnection.Tls"/>; a listener without it
    /// still works, and its connections have no TLS details.
    /// </summary>
    public interface ITlsListener : IConnectionListener
    {
        /// <summary>The TLS details of the connection of a sender handle; null when unknown.</summary>
        TlsConnectionInfo GetTlsInfo(object sender);
    }
}
