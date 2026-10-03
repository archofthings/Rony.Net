using System;
using System.Globalization;
using System.Text;

namespace Rony.Helpers
{
    /// <summary>Makes text that a client controls (server name, certificate subject, endpoint, exception message) safe for one log line.</summary>
    internal static class LogText
    {
        internal const int MaxLength = 256;

        /// <summary>
        /// Replaces control, line/paragraph separator and Unicode format characters with <c>\xNN</c> / <c>\uNNNN</c> escapes
        /// and cuts the result at <see cref="MaxLength"/> characters (then appends "…").
        /// </summary>
        public static string Safe(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            var result = new StringBuilder(Math.Min(text.Length, MaxLength + 1));
            foreach (var c in text)
            {
                if (result.Length >= MaxLength)
                {
                    result.Append('…');
                    break;
                }

                if (char.IsControl(c) || c == '\u2028' || c == '\u2029' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                    result.Append(c < 0x100 ? "\\x" + ((int)c).ToString("X2", CultureInfo.InvariantCulture) : "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else
                    result.Append(c);
            }

            return result.ToString();
        }

        /// <summary>The safe form of <paramref name="exception"/> as <c>Type: message</c>.</summary>
        public static string Describe(Exception exception) => $"{exception.GetType().Name}: {Safe(exception.Message)}";
    }
}
