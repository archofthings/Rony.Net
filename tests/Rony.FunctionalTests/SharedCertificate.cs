using System;
using System.Security.Cryptography.X509Certificates;

namespace Rony.FunctionalTests
{
    /// <summary>
    /// One self-signed "localhost" certificate shared by the SSL tests, created once with the built-in
    /// <see cref="Rony.Net.TestCertificate"/>.
    /// </summary>
    internal static class SharedCertificate
    {
        public const string SubjectName = "localhost";

        private static readonly Lazy<X509Certificate2> _instance = new Lazy<X509Certificate2>(() => Rony.Net.TestCertificate.CreateSelfSigned(SubjectName));

        public static X509Certificate2 Instance => _instance.Value;
    }
}
