using System;
using System.Globalization;
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
                    return "\"" + EscapeFormatCharacters(text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")) + "\"";
            }
            catch (DecoderFallbackException)
            {
            }

            return string.Join(" ", data.Select(b => "0x" + b.ToString("X2")));
        }

        // Format characters (for example U+202E, right-to-left override) can disguise a log line.
        private static string EscapeFormatCharacters(string text)
        {
            if (!text.Any(c => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)) return text;

            var result = new StringBuilder(text.Length);
            foreach (var c in text)
                result.Append(CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format ? "\\u" + ((int)c).ToString("X4") : c.ToString());
            return result.ToString();
        }
    }
}
