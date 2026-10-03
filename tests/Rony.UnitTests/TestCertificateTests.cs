using Rony.Net;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Rony.UnitTests
{
    public class TestCertificateTests
    {
        [Theory]
        [InlineData("localhost", true, true)]
        [InlineData("my-client", true, false)]
        [InlineData("10.1.2.3", false, false)]
        public void CreateSelfSigned_Should_Create_A_Certificate_With_A_Private_Key_Subject_And_Names(string subjectName, bool hasDnsName, bool hasLoopback)
        {
            //Act
            using var certificate = TestCertificate.CreateSelfSigned(subjectName);

            //Assert
            Assert.True(certificate.HasPrivateKey);
            Assert.Equal("CN=" + subjectName, certificate.Subject);
            var names = certificate.Extensions["2.5.29.17"].Format(false);   // subject alternative name; the text format depends on the OS
            Assert.Contains(subjectName, names);
            Assert.Equal(hasDnsName, names.Contains("DNS"));
            Assert.Equal(hasLoopback, names.Contains("127.0.0.1"));
            var usages = ((X509EnhancedKeyUsageExtension)certificate.Extensions["2.5.29.37"]).EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value);
            Assert.Equal(new[] { "1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2" }, usages);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void CreateSelfSigned_Should_Throw_For_A_Missing_Subject_Name(string subjectName)
        {
            Assert.Throws<ArgumentException>(() => TestCertificate.CreateSelfSigned(subjectName));
        }
    }
}
