using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    /// <summary>
    /// A TCP server secured with SSL/TLS. Clients must complete a TLS handshake before sending requests.
    /// </summary>
    public class TcpServerSsl : TcpServerBase
    {
        private readonly Lazy<X509Certificate> _certificate;
        private readonly SslProtocols _protocol;
        private volatile bool _failHandshake;

        /// <summary>
        /// When true, every new TLS handshake fails: the server answers the client's hello with a fatal
        /// <c>handshake_failure</c> alert and closes the connection, which never shows up in
        /// <c>MockServer.Connections</c>. Can be changed while the server runs; it applies to new connections.
        /// </summary>
        public bool FailHandshake
        {
            get => _failHandshake;
            set => _failHandshake = value;
        }

        /// <summary>
        /// Creates an SSL/TLS server which uses the given certificate. The certificate must contain a private key.
        /// Use port 0 to let the operating system pick a free port.
        /// </summary>
        public TcpServerSsl(IPAddress address, int port, X509Certificate certificate, SslProtocols protocol)
            : base(address, port)
        {
            if (certificate == null) throw new ArgumentNullException(nameof(certificate));
            _certificate = new Lazy<X509Certificate>(() => certificate);
            _protocol = protocol;
        }

        /// <summary>Listens on 127.0.0.1 with the given certificate.</summary>
        public TcpServerSsl(int port, X509Certificate certificate, SslProtocols protocol)
            : this(IPAddress.Loopback, port, certificate, protocol)
        {
        }

        /// <summary>Listens on the given IP address with the given certificate.</summary>
        public TcpServerSsl(string address, int port, X509Certificate certificate, SslProtocols protocol)
            : this(IPAddress.Parse(address), port, certificate, protocol)
        {
        }

        /// <summary>
        /// Creates an SSL/TLS server which uses an installed certificate, looked up by subject name
        /// in the CurrentUser and LocalMachine "My" stores. You need read permission on its private key.
        /// </summary>
        public TcpServerSsl(IPAddress address, int port, string certificateName, SslProtocols protocol)
            : base(address, port)
        {
            _certificate = new Lazy<X509Certificate>(() => FindCertificate(certificateName));
            _protocol = protocol;
        }

        /// <summary>Listens on 127.0.0.1 with an installed certificate, looked up by subject name.</summary>
        public TcpServerSsl(int port, string certificateName, SslProtocols protocol)
            : this(IPAddress.Loopback, port, certificateName, protocol)
        {
        }

        /// <summary>Listens on the given IP address with an installed certificate, looked up by subject name.</summary>
        public TcpServerSsl(string address, int port, string certificateName, SslProtocols protocol)
            : this(IPAddress.Parse(address), port, certificateName, protocol)
        {
        }

        /// <inheritdoc />
        protected override async Task<Stream> OpenStreamAsync(TcpClient client)
        {
            if (FailHandshake)
            {
                // Wait for the ClientHello, then answer with a fatal alert: handshake_failure.
                var networkStream = client.GetStream();
                // Read the whole hello record, so closing the socket does not reset the connection and discard the alert.
                var header = new byte[5];
                if (await ReadFullyAsync(networkStream, header).ConfigureAwait(false))
                    await ReadFullyAsync(networkStream, new byte[(header[3] << 8) | header[4]]).ConfigureAwait(false);
                var alert = new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 };
                await networkStream.WriteAsync(alert, 0, alert.Length).ConfigureAwait(false);
                await networkStream.FlushAsync().ConfigureAwait(false);
                try
                {
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (Exception)
                {
                    // The client is already gone; the alert was best effort.
                }
                throw new AuthenticationException("The TLS handshake was failed on purpose (FailHandshake).");
            }

            var sslStream = new SslStream(client.GetStream(), false);
            try
            {
                await sslStream.AuthenticateAsServerAsync(_certificate.Value, false, _protocol, false).ConfigureAwait(false);
                return sslStream;
            }
            catch
            {
                sslStream.Dispose();
                throw;
            }
        }

        /// <summary>Fills <paramref name="buffer"/> from the stream; false when the stream ended first.</summary>
        private static async Task<bool> ReadFullyAsync(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
                if (read == 0) return false;
                total += read;
            }

            return true;
        }

        private static X509Certificate FindCertificate(string subjectName)
        {
            foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
            {
                using var store = new X509Store(StoreName.My, location);
                try
                {
                    store.Open(OpenFlags.ReadOnly);
                }
                catch (CryptographicException)
                {
                    continue;
                }

                var certificates = store.Certificates.Find(X509FindType.FindBySubjectName, subjectName, false);
                foreach (var certificate in certificates)
                {
                    if (certificate.HasPrivateKey)
                        return certificate;
                }
            }

            throw new InvalidOperationException(
                $"No certificate with subject name '{subjectName}' and an accessible private key was found " +
                "in the CurrentUser or LocalMachine 'My' certificate stores.");
        }
    }
}
