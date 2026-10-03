using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rony.Net
{
    /// <summary>
    /// Creates certificates for TLS tests, in memory. Nothing gets installed.
    /// </summary>
    public static class TestCertificate
    {
        /// <summary>
        /// Creates a self-signed certificate with a private key, valid from five minutes ago for seven days. It can be
        /// used as a server certificate and as a client certificate (mutual TLS). The subject is
        /// <c>CN=<paramref name="subjectName"/></c>; the subject alternative names are the IP address when
        /// <paramref name="subjectName"/> is one and the DNS name otherwise ("localhost" also gets 127.0.0.1 and ::1).
        /// The caller owns the certificate and disposes it; every call creates a new one.
        /// </summary>
        /// <param name="subjectName">The common name; "localhost" by default.</param>
        /// <exception cref="ArgumentException"><paramref name="subjectName"/> is null or empty.</exception>
        public static X509Certificate2 CreateSelfSigned(string subjectName = "localhost")
        {
            if (string.IsNullOrEmpty(subjectName))
                throw new ArgumentException("The subject name must not be null or empty.", nameof(subjectName));

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, false)); // server and client authentication

            var names = new SubjectAlternativeNameBuilder();
            if (IPAddress.TryParse(subjectName, out var address))
            {
                names.AddIpAddress(address);
            }
            else
            {
                names.AddDnsName(subjectName);
                if (string.Equals(subjectName, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    names.AddIpAddress(IPAddress.Loopback);
                    names.AddIpAddress(IPAddress.IPv6Loopback);
                }
            }
            request.CertificateExtensions.Add(names.Build());

            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
            // Export and re-import so the private key works with SslStream on every OS (Windows needs this).
            return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
        }
    }
}
