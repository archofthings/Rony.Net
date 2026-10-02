using Rony.Interfaces;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

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
            new LengthPrefixFraming(prefixLength, bigEndian, false);

        /// <summary>
        /// Like <see cref="LengthPrefix(int, bool)"/>, but with <paramref name="includesPrefix"/> set to true the length
        /// is the size of the whole frame, the prefix itself plus the message. A received length smaller than
        /// <paramref name="prefixLength"/> is invalid: that connection is closed and the failure is reported through
        /// <c>ConnectionFailed</c> and the log, while the server keeps running. Messages decoded earlier in the same
        /// burst are dropped with the connection.
        /// </summary>
        public static IMessageFraming LengthPrefix(int prefixLength, bool bigEndian, bool includesPrefix) =>
            new LengthPrefixFraming(prefixLength, bigEndian, includesPrefix);

        /// <summary>
        /// Every message is exactly <paramref name="length"/> bytes. Requests are delivered as they are (padding is not
        /// removed). A response shorter than <paramref name="length"/> is padded on the right with
        /// <paramref name="padding"/>; a longer one cannot be sent and throws <see cref="InvalidOperationException"/>.
        /// </summary>
        public static IMessageFraming FixedLength(int length, byte padding = 0) => new FixedLengthFraming(length, padding);

        /// <summary>
        /// Every message is wrapped in a <paramref name="start"/> and an <paramref name="end"/> byte. The markers are
        /// removed from requests and added to every response; bytes outside a message are ignored. There is no escaping
        /// and no checksum, so an <paramref name="end"/> byte inside the payload ends the message.
        /// </summary>
        public static IMessageFraming StartEnd(byte start, byte end) => new StartEndFraming(start, end);

        /// <summary>
        /// Messages wrapped in STX (0x02) and ETX (0x03), the same as <c>StartEnd(0x02, 0x03)</c>. There is no escaping,
        /// so an ETX byte inside the payload ends the message.
        /// </summary>
        public static IMessageFraming StxEtx { get; } = new StartEndFraming(0x02, 0x03);

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
            private readonly bool _includesPrefix;

            public LengthPrefixFraming(int prefixLength, bool bigEndian, bool includesPrefix)
            {
                if (prefixLength != 1 && prefixLength != 2 && prefixLength != 4)
                    throw new ArgumentOutOfRangeException(nameof(prefixLength), "The length prefix must be 1, 2 or 4 bytes.");
                _prefixLength = prefixLength;
                _bigEndian = bigEndian;
                _includesPrefix = includesPrefix;
            }

            public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)
            {
                var messages = new List<byte[]>();
                consumed = 0;
                while (data.Length - consumed >= _prefixLength)
                {
                    var length = ReadLength(data.Slice(consumed, _prefixLength));
                    if (_includesPrefix)
                    {
                        if (length < _prefixLength)
                            throw new InvalidDataException($"Invalid frame: the length {length} is smaller than the {_prefixLength}-byte length prefix it includes.");
                        length -= _prefixLength;
                    }

                    if (length < 0 || data.Length - consumed - _prefixLength < length) break;
                    messages.Add(data.Slice(consumed + _prefixLength, (int)length).ToArray());
                    consumed += _prefixLength + (int)length;
                }

                return messages;
            }

            public byte[] Encode(byte[] response)
            {
                var maxLength = _prefixLength == 4 ? int.MaxValue : (1L << (8 * _prefixLength)) - 1;
                var value = (long)response.Length + (_includesPrefix ? _prefixLength : 0);
                if (value > maxLength)
                    throw new InvalidOperationException($"The response is too long for a {_prefixLength}-byte length prefix.");

                var result = new byte[_prefixLength + response.Length];
                var prefix = result.AsSpan(0, _prefixLength);
                switch (_prefixLength)
                {
                    case 1:
                        prefix[0] = (byte)value;
                        break;
                    case 2 when _bigEndian:
                        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)value);
                        break;
                    case 2:
                        BinaryPrimitives.WriteUInt16LittleEndian(prefix, (ushort)value);
                        break;
                    case 4 when _bigEndian:
                        BinaryPrimitives.WriteInt32BigEndian(prefix, (int)value);
                        break;
                    default:
                        BinaryPrimitives.WriteInt32LittleEndian(prefix, (int)value);
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

        private sealed class FixedLengthFraming : IMessageFraming
        {
            private readonly int _length;
            private readonly byte _padding;

            public FixedLengthFraming(int length, byte padding)
            {
                if (length <= 0)
                    throw new ArgumentOutOfRangeException(nameof(length), "The message length must be greater than zero.");
                _length = length;
                _padding = padding;
            }

            public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)
            {
                var messages = new List<byte[]>();
                consumed = 0;
                while (data.Length - consumed >= _length)
                {
                    messages.Add(data.Slice(consumed, _length).ToArray());
                    consumed += _length;
                }

                return messages;
            }

            public byte[] Encode(byte[] response)
            {
                if (response.Length > _length)
                    throw new InvalidOperationException($"The response is too long for a {_length}-byte fixed length.");
                if (response.Length == _length) return response;

                var result = new byte[_length];
                Buffer.BlockCopy(response, 0, result, 0, response.Length);
                result.AsSpan(response.Length).Fill(_padding);
                return result;
            }
        }

        private sealed class StartEndFraming : IMessageFraming
        {
            private readonly byte _start;
            private readonly byte _end;

            public StartEndFraming(byte start, byte end)
            {
                if (start == end)
                    throw new ArgumentException("The start and end bytes must be different.", nameof(end));
                _start = start;
                _end = end;
            }

            public IReadOnlyList<byte[]> Decode(ReadOnlySpan<byte> data, bool endOfBurst, out int consumed)
            {
                var messages = new List<byte[]>();
                consumed = 0;
                while (true)
                {
                    var start = data.Slice(consumed).IndexOf(_start);
                    if (start < 0)
                    {
                        consumed = data.Length;   // no message start: drop the noise
                        break;
                    }

                    var bodyStart = consumed + start + 1;
                    var end = data.Slice(bodyStart).IndexOf(_end);
                    if (end < 0)
                    {
                        consumed += start;        // incomplete message: wait for more data
                        break;
                    }

                    messages.Add(data.Slice(bodyStart, end).ToArray());
                    consumed = bodyStart + end + 1;
                }

                return messages;
            }

            public byte[] Encode(byte[] response)
            {
                var result = new byte[response.Length + 2];
                result[0] = _start;
                Buffer.BlockCopy(response, 0, result, 1, response.Length);
                result[result.Length - 1] = _end;
                return result;
            }
        }
    }
}
