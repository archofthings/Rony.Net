using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rony.FunctionalTests
{
    /// <summary>
    /// Creates a self-signed "localhost" certificate, so the SSL tests run without installing anything.
    /// </summary>
    internal static class TestCertificate
    {
        public const string SubjectName = "localhost";

        private static readonly Lazy<X509Certificate2> _instance = new Lazy<X509Certificate2>(Create);

        public static X509Certificate2 Instance => _instance.Value;

        private static X509Certificate2 Create()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={SubjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // Server authentication
            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName(SubjectName);
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());

            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            // Round-trip through PFX so the private key is usable by SslStream on every platform.
            return new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string)null, X509KeyStorageFlags.Exportable);
        }
    }
}
