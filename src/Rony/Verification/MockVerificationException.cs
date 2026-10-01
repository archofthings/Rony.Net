using System;

namespace Rony.Net
{
    /// <summary>
    /// Thrown when a <c>Verify...</c> call on the mock fails.
    /// </summary>
    public class MockVerificationException : Exception
    {
        /// <summary>Creates the exception with a description of what was expected and received.</summary>
        public MockVerificationException(string message) : base(message)
        {
        }
    }
}
