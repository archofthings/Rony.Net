using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rony.Samples;

public static class TestCertificates
{
    /// <summary>
    /// Creates a self-signed certificate for "localhost", valid for one day. Nothing gets installed.
    /// </summary>
    public static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false)); // server authentication
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Export and re-import so the private key works with SslStream on every OS (Windows needs this).
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }
}
