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
    public class TcpServerSsl : TcpServerBase
    {
        private readonly Lazy<X509Certificate> _certificate;
        private readonly SslProtocols _protocol;

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

        public TcpServerSsl(int port, X509Certificate certificate, SslProtocols protocol)
            : this(IPAddress.Loopback, port, certificate, protocol)
        {
        }

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

        public TcpServerSsl(int port, string certificateName, SslProtocols protocol)
            : this(IPAddress.Loopback, port, certificateName, protocol)
        {
        }

        public TcpServerSsl(string address, int port, string certificateName, SslProtocols protocol)
            : this(IPAddress.Parse(address), port, certificateName, protocol)
        {
        }

        protected override async Task<Stream> OpenStreamAsync(TcpClient client)
        {
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
