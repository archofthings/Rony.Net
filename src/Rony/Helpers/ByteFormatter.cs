using System;
using System.Linq;
using System.Text;

namespace Rony.Helpers
{
    internal static class ByteFormatter
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>
        /// Readable form of a payload: quoted text when it is printable UTF-8, hex bytes otherwise.
        /// </summary>
        public static string Describe(byte[] data)
        {
            if (data == null) return "null";
            if (data.Length == 0) return "<empty>";

            try
            {
                var text = StrictUtf8.GetString(data);
                if (text.All(c => !char.IsControl(c) || c == '\r' || c == '\n' || c == '\t'))
                    return "\"" + text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
            }
            catch (DecoderFallbackException)
            {
            }

            return string.Join(" ", data.Select(b => "0x" + b.ToString("X2")));
        }
    }
}
