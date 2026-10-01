using System.Text;

namespace Rony
{
    /// <summary>
    /// UTF-8 conversion helpers used throughout the library and handy in tests.
    /// </summary>
    public static class Convertor
    {
        /// <summary>Decodes UTF-8 bytes to a string.</summary>
        public static string GetString(this byte[] input) => Encoding.UTF8.GetString(input);

        /// <summary>Encodes a string as UTF-8 bytes.</summary>
        public static byte[] GetBytes(this string input) => Encoding.UTF8.GetBytes(input);
    }
}
