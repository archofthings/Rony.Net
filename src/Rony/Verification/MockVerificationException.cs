using System;

namespace Rony.Net
{
    /// <summary>
    /// Thrown when a <c>Verify...</c> call on the mock fails.
    /// </summary>
    public class MockVerificationException : Exception
    {
        public MockVerificationException(string message) : base(message)
        {
        }
    }
}
