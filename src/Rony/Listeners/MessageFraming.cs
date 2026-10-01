using Rony.Interfaces;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Rony.Listeners
{
    /// <summary>
    /// Built-in ways to split a TCP stream into messages.
    /// </summary>
    public static class MessageFraming
    {
        /// <summary>
        /// Everything that arrives in one burst is one message, and responses are sent as they are. This is the default.
        /// </summary>
        public static IMessageFraming None { get; } = new NoFraming();

        /// <summary>
        /// Messages end with <paramref name="delimiter"/> (for example "\n" or "\r\n"). The delimiter is removed from
        /// requests before matching and appended to every response.
        /// </summary>
        public static IMessageFraming Delimiter(string delimiter) => Delimiter((delimiter ?? string.Empty).GetBytes());

        /// <inheritdoc cref="Delimiter(string)"/>
        public static IMessageFraming Delimiter(byte[] delimiter) => new DelimiterFraming(delimiter);

        /// <summary>
        /// Every message starts with its length as an unsigned integer of <paramref name="prefixLength"/> bytes
        /// (1, 2 or 4). The prefix is removed from requests before matching and added to every response.
        /// </summary>
        public static IMessageFraming LengthPrefix(int prefixLength = 4, bool bigEndian = true) =>
            new LengthPrefixFraming(prefixLength, bigEndian);

        private sealed class NoFraming : IMessageFraming
        {
            public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)
            {
                if (!endOfBurst || data.IsEmpty)
                {
                    consumed = 0;
                    return Array.Empty<byte[]>();
                }

                consumed = data.Length;
                return new[] { data.ToArray() };
            }

            public byte[] Encode(byte[] response) => response;
        }

        private sealed class DelimiterFraming : IMessageFraming
        {
            private readonly byte[] _delimiter;

            public DelimiterFraming(byte[] delimiter)
            {
                if (delimiter == null || delimiter.Length == 0)
                    throw new ArgumentException("The delimiter must not be empty.", nameof(delimiter));
                _delimiter = delimiter;
            }

            public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)
            {
                var messages = new List<byte[]>();
                consumed = 0;
                while (true)
                {
                    var index = data.Slice(consumed).IndexOf(_delimiter);
                    if (index < 0) break;
                    messages.Add(data.Slice(consumed, index).ToArray());
                    consumed += index + _delimiter.Length;
                }

                return messages;
            }

            public byte[] Encode(byte[] response)
            {
                var result = new byte[response.Length + _delimiter.Length];
                Buffer.BlockCopy(response, 0, result, 0, response.Length);
                Buffer.BlockCopy(_delimiter, 0, result, response.Length, _delimiter.Length);
                return result;
            }
        }

        private sealed class LengthPrefixFraming : IMessageFraming
        {
            private readonly int _prefixLength;
            private readonly bool _bigEndian;

            public LengthPrefixFraming(int prefixLength, bool bigEndian)
            {
                if (prefixLength != 1 && prefixLength != 2 && prefixLength != 4)
                    throw new ArgumentOutOfRangeException(nameof(prefixLength), "The length prefix must be 1, 2 or 4 bytes.");
                _prefixLength = prefixLength;
                _bigEndian = bigEndian;
            }

            public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)
            {
                var messages = new List<byte[]>();
                consumed = 0;
                while (data.Length - consumed >= _prefixLength)
                {
                    var length = ReadLength(data.Slice(consumed, _prefixLength));
                    if (length < 0 || data.Length - consumed - _prefixLength < length) break;
                    messages.Add(data.Slice(consumed + _prefixLength, (int)length).ToArray());
                    consumed += _prefixLength + (int)length;
                }

                return messages;
            }

            public byte[] Encode(byte[] response)
            {
                var maxLength = _prefixLength == 4 ? int.MaxValue : (1L << (8 * _prefixLength)) - 1;
                if (response.Length > maxLength)
                    throw new InvalidOperationException($"The response is too long for a {_prefixLength}-byte length prefix.");

                var result = new byte[_prefixLength + response.Length];
                var prefix = result.AsSpan(0, _prefixLength);
                switch (_prefixLength)
                {
                    case 1:
                        prefix[0] = (byte)response.Length;
                        break;
                    case 2 when _bigEndian:
                        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)response.Length);
                        break;
                    case 2:
                        BinaryPrimitives.WriteUInt16LittleEndian(prefix, (ushort)response.Length);
                        break;
                    case 4 when _bigEndian:
                        BinaryPrimitives.WriteInt32BigEndian(prefix, response.Length);
                        break;
                    default:
                        BinaryPrimitives.WriteInt32LittleEndian(prefix, response.Length);
                        break;
                }

                Buffer.BlockCopy(response, 0, result, _prefixLength, response.Length);
                return result;
            }

            private long ReadLength(ReadOnlySpan<byte> prefix)
            {
                switch (_prefixLength)
                {
                    case 1:
                        return prefix[0];
                    case 2:
                        return _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(prefix) : BinaryPrimitives.ReadUInt16LittleEndian(prefix);
                    default:
                        return _bigEndian ? BinaryPrimitives.ReadInt32BigEndian(prefix) : BinaryPrimitives.ReadInt32LittleEndian(prefix);
                }
            }
        }
    }
}
