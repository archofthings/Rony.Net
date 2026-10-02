using Rony.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rony.Helpers
{
    /// <summary>Reads and writes JSON (RFC 8259) for <see cref="JsonValue"/>, without a dependency.</summary>
    internal static class JsonParser
    {
        private const int MaxDepth = 256;

        /// <summary>Parses a whole document; throws <see cref="FormatException"/> with the position if it is invalid.</summary>
        public static JsonValue Parse(string json)
        {
            var reader = new Reader(json);
            reader.SkipWhitespace();
            var value = reader.ReadValue(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd) throw reader.Error("Unexpected content after the JSON value");
            return value;
        }

        /// <summary>The text as a JSON string literal, with quotes and escapes.</summary>
        public static string Quote(string text)
        {
            var builder = new StringBuilder(text.Length + 2).Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else builder.Append(c);
                        break;
                }
            }
            return builder.Append('"').ToString();
        }

        private sealed class Reader
        {
            private readonly string _text;
            private int _position;

            public Reader(string text)
            {
                _text = text;
            }

            public bool AtEnd => _position >= _text.Length;

            public FormatException Error(string message) =>
                new FormatException($"{message} at position {_position}.");

            public void SkipWhitespace()
            {
                while (_position < _text.Length && (_text[_position] == ' ' || _text[_position] == '\t' || _text[_position] == '\n' || _text[_position] == '\r'))
                    _position++;
            }

            public JsonValue ReadValue(int depth)
            {
                if (AtEnd) throw Error("Unexpected end of JSON");
                switch (_text[_position])
                {
                    case '{': return ReadObject(depth + 1);
                    case '[': return ReadArray(depth + 1);
                    case '"': return JsonValue.FromString(ReadString());
                    case 't': ReadLiteral("true"); return JsonValue.FromBoolean(true);
                    case 'f': ReadLiteral("false"); return JsonValue.FromBoolean(false);
                    case 'n': ReadLiteral("null"); return JsonValue.Null();
                    default: return ReadNumber();
                }
            }

            private JsonValue ReadObject(int depth)
            {
                if (depth > MaxDepth) throw Error("The JSON is nested too deeply");
                _position++;
                var properties = new Dictionary<string, JsonValue>();
                SkipWhitespace();
                if (Peek('}')) return JsonValue.FromObject(properties);
                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[_position] != '"') throw Error("Expected a property name");
                    var name = ReadString();
                    SkipWhitespace();
                    if (AtEnd || _text[_position] != ':') throw Error("Expected ':'");
                    _position++;
                    SkipWhitespace();
                    properties[name] = ReadValue(depth);
                    SkipWhitespace();
                    if (Peek('}')) return JsonValue.FromObject(properties);
                    if (AtEnd || _text[_position] != ',') throw Error("Expected ',' or '}'");
                    _position++;
                }
            }

            private JsonValue ReadArray(int depth)
            {
                if (depth > MaxDepth) throw Error("The JSON is nested too deeply");
                _position++;
                var items = new List<JsonValue>();
                SkipWhitespace();
                if (Peek(']')) return JsonValue.FromArray(items);
                while (true)
                {
                    SkipWhitespace();
                    items.Add(ReadValue(depth));
                    SkipWhitespace();
                    if (Peek(']')) return JsonValue.FromArray(items);
                    if (AtEnd || _text[_position] != ',') throw Error("Expected ',' or ']'");
                    _position++;
                }
            }

            /// <summary>Consumes <paramref name="c"/> if it is next.</summary>
            private bool Peek(char c)
            {
                if (AtEnd || _text[_position] != c) return false;
                _position++;
                return true;
            }

            private void ReadLiteral(string literal)
            {
                if (string.CompareOrdinal(_text, _position, literal, 0, literal.Length) != 0)
                    throw Error("Invalid JSON value");
                _position += literal.Length;
            }

            private string ReadString()
            {
                _position++;
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("Unterminated string");
                    var c = _text[_position];
                    if (c == '"')
                    {
                        _position++;
                        return builder.ToString();
                    }
                    if (c < 0x20) throw Error("Unescaped control character in string");
                    if (c != '\\')
                    {
                        builder.Append(c);
                        _position++;
                        continue;
                    }

                    _position++;
                    if (AtEnd) throw Error("Unterminated string");
                    switch (_text[_position])
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_position + 4 >= _text.Length
                                || !int.TryParse(_text.Substring(_position + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                                throw Error("Invalid \\u escape");
                            builder.Append((char)code);
                            _position += 4;
                            break;
                        default:
                            throw Error("Invalid escape sequence");
                    }
                    _position++;
                }
            }

            private JsonValue ReadNumber()
            {
                var start = _position;
                Peek('-');
                if (Peek('0')) { }
                else if (!ReadDigits()) throw Error("Invalid JSON value");
                if (Peek('.') && !ReadDigits()) throw Error("Invalid number");
                if (Peek('e') || Peek('E'))
                {
                    if (!Peek('+')) Peek('-');
                    if (!ReadDigits()) throw Error("Invalid number");
                }

                var text = _text.Substring(start, _position - start);
                return JsonValue.FromNumber(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture), text);
            }

            private bool ReadDigits()
            {
                var start = _position;
                while (_position < _text.Length && _text[_position] >= '0' && _text[_position] <= '9')
                    _position++;
                return _position > start;
            }
        }
    }
}
