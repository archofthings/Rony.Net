using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Rony.Models
{
    /// <summary>
    /// What was negotiated in the TLS handshake of a connection; see <see cref="ClientConnection.Tls"/>.
    /// </summary>
    public sealed class TlsConnectionInfo
    {
        /// <summary>Creates the TLS details of a connection.</summary>
        /// <param name="protocol">The negotiated protocol.</param>
        /// <param name="serverName">The SNI host name the client sent; null when it sent none.</param>
        /// <param name="clientCertificate">The certificate the client presented; null when it presented none.</param>
        public TlsConnectionInfo(SslProtocols protocol, string serverName, X509Certificate2 clientCertificate)
        {
            Protocol = protocol;
            ServerName = serverName;
            ClientCertificate = clientCertificate;
        }

        /// <summary>The negotiated protocol, such as <see cref="SslProtocols.Tls12"/> or <c>Tls13</c>.</summary>
        public SslProtocols Protocol { get; }

        /// <summary>The SNI host name the client sent; null when it sent none.</summary>
        public string ServerName { get; }

        /// <summary>The certificate the client presented; null when it presented none.</summary>
        public X509Certificate2 ClientCertificate { get; }
    }
}
