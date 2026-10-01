using Rony.Interfaces;
using Rony.Models;
using Rony.Wrappers;
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    public class TcpServerSsl : IListener
    {
        private readonly TcpListenerWrapper _listener;
        private readonly Lazy<X509Certificate> _certificate;
        private readonly SslProtocols _protocol;

        public IPAddress Address { get; set; }
        public int Port { get; set; }
        public bool Active => _listener.Active;

        /// <summary>
        /// Creates an SSL/TLS server which uses the given certificate. The certificate must contain a private key.
        /// </summary>
        public TcpServerSsl(IPAddress address, int port, X509Certificate certificate, SslProtocols protocol)
        {
            if (certificate == null) throw new ArgumentNullException(nameof(certificate));
            _certificate = new Lazy<X509Certificate>(() => certificate);
            _protocol = protocol;
            _listener = new TcpListenerWrapper(address, port);
            Address = address;
            Port = port;
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
        {
            _certificate = new Lazy<X509Certificate>(() => FindCertificate(certificateName));
            _protocol = protocol;
            _listener = new TcpListenerWrapper(address, port);
            Address = address;
            Port = port;
        }

        public TcpServerSsl(int port, string certificateName, SslProtocols protocol)
            : this(IPAddress.Loopback, port, certificateName, protocol)
        {
        }

        public TcpServerSsl(string address, int port, string certificateName, SslProtocols protocol)
            : this(IPAddress.Parse(address), port, certificateName, protocol)
        {
        }

        public async Task<Message> ReceiveAsync()
        {
            var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            SslStream sslStream = null;
            try
            {
                sslStream = new SslStream(client.GetStream(), false);
                await sslStream.AuthenticateAsServerAsync(_certificate.Value, false, _protocol, false).ConfigureAwait(false);
                var buffer = new byte[client.ReceiveBufferSize];
                var readBytes = await sslStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                using var body = new MemoryStream();
                body.Write(buffer, 0, readBytes);

                return new Message(body.ToArray(), sslStream);
            }
            catch
            {
                sslStream?.Dispose();
                client.Dispose();
                throw;
            }
        }

        public async Task ReplyAsync(string response, object sender)
        {
            await ReplyAsync(response.GetBytes(), sender).ConfigureAwait(false);
        }

        public async Task ReplyAsync(byte[] response, object sender)
        {
            using var sslStream = (SslStream)sender;
            if (response.Length > 0)
                await sslStream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
        }

        public void Start()
        {
            _listener.Start();
        }

        public void Stop()
        {
            _listener.Stop();
        }

        public void Dispose()
        {
            _listener.Stop();
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
