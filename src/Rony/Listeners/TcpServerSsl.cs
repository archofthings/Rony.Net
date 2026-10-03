using Rony.Interfaces;
using Rony.Models;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    /// <summary>
    /// A TCP server secured with SSL/TLS. Clients must complete a TLS handshake before sending requests.
    /// </summary>
    public class TcpServerSsl : TcpServerBase, ITlsListener
    {
        private readonly Lazy<X509Certificate> _certificate;
        private readonly SslProtocols _protocol;
        private volatile bool _failHandshake;
        private volatile bool _requireClientCertificate;
        private volatile Func<X509Certificate2, bool> _clientCertificateValidator;

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
        /// When true the server asks for a client certificate (mutual TLS) and fails the handshake of a client that
        /// sends none. A presented certificate is accepted whatever its chain or trust errors (test certificates are
        /// self-signed), unless <see cref="ClientCertificateValidator"/> rejects it. Revocation is not checked.
        /// Can be changed while the server runs; it applies to new connections.
        /// </summary>
        public bool RequireClientCertificate
        {
            get => _requireClientCertificate;
            set => _requireClientCertificate = value;
        }

        /// <summary>
        /// Decides whether a presented client certificate is accepted; null (the default) accepts every certificate.
        /// The certificate it receives is the one later available as <c>connection.Tls.ClientCertificate</c>, so it must
        /// not be disposed by the validator. A validator which throws rejects the certificate. Only used with <see cref="RequireClientCertificate"/>.
        /// Can be changed while the server runs; it applies to new connections.
        /// </summary>
        public Func<X509Certificate2, bool> ClientCertificateValidator
        {
            get => _clientCertificateValidator;
            set => _clientCertificateValidator = value;
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

            var requireClientCertificate = RequireClientCertificate;
            var validator = ClientCertificateValidator;
            string rejection = null;
            string serverName = null;
            X509Certificate2 validatedCertificate = null;
            var sslStream = new TlsStream(client.GetStream());
            try
            {
                var certificate = _certificate.Value;
                var options = new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, hostName) =>
                    {
                        serverName = string.IsNullOrEmpty(hostName) ? null : hostName;
                        return certificate;
                    },
                    ClientCertificateRequired = requireClientCertificate,
                    EnabledSslProtocols = _protocol,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                };
                if (requireClientCertificate)
                {
                    options.RemoteCertificateValidationCallback = (_, clientCertificate, _, _) =>
                    {
                        if (clientCertificate == null)
                        {
                            rejection = "The client sent no certificate, but RequireClientCertificate is set.";
                            return false;
                        }

                        if (validator == null) return true;
                        // The callback may run more than once per handshake: keep only the latest copy.
                        validatedCertificate?.Dispose();
                        validatedCertificate = null;
                        X509Certificate2 copy = null;
                        try
                        {
                            copy = new X509Certificate2(clientCertificate);
                            if (validator(copy))
                            {
                                validatedCertificate = copy;
                                return true;
                            }

                            rejection = "The client certificate was rejected by ClientCertificateValidator.";
                        }
                        catch (Exception exception)
                        {
                            rejection = "The client certificate was rejected because ClientCertificateValidator threw: " + exception.Message;
                        }

                        copy?.Dispose();
                        return false;
                    };
                }

                try
                {
                    await sslStream.AuthenticateAsServerAsync(options, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (rejection != null)
                {
                    throw new AuthenticationException(rejection, exception);
                }

                var remoteCertificate = sslStream.RemoteCertificate;
                var clientCertificateCopy = validatedCertificate
                    ?? (remoteCertificate == null ? null : new X509Certificate2(remoteCertificate));
                validatedCertificate = null;
                sslStream.Info = new TlsConnectionInfo(sslStream.SslProtocol, serverName, clientCertificateCopy);
                return sslStream;
            }
            catch
            {
                validatedCertificate?.Dispose();
                sslStream.Dispose();
                throw;
            }
        }

        /// <inheritdoc />
        public TlsConnectionInfo GetTlsInfo(object sender) => ((sender as TcpConnection)?.Stream as TlsStream)?.Info;

        /// <summary>An <see cref="SslStream"/> which carries the TLS details captured after its handshake.</summary>
        private sealed class TlsStream : SslStream
        {
            public TlsStream(Stream innerStream) : base(innerStream, false)
            {
            }

            public TlsConnectionInfo Info { get; set; }
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
