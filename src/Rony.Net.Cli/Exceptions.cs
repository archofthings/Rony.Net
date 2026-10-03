using System;

namespace Rony.Cli
{
    /// <summary>Bad command line: reported with a hint to the usage and exit code 2.</summary>
    internal sealed class UsageException : Exception
    {
        public UsageException(string message) : base(message)
        {
        }
    }

    /// <summary>An invalid or missing input or output file: reported as it is, with exit code 2.</summary>
    internal sealed class InputException : Exception
    {
        public InputException(string message) : base(message)
        {
        }
    }
}
