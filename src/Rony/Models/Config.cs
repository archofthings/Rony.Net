using System;

namespace Rony.Models
{
    public class Config
    {
        private readonly byte[] _response;
        private readonly Func<string, string> _stringFunc;
        private readonly Func<byte[], byte[]> _byteFunc;

        public Config(string response) : this((response ?? string.Empty).GetBytes())
        {
        }

        public Config(byte[] response)
        {
            _response = response ?? new byte[0];
        }

        public Config(Func<string, string> stringFunc)
        {
            _stringFunc = stringFunc ?? throw new ArgumentNullException(nameof(stringFunc));
        }

        public Config(Func<byte[], byte[]> byteFunc)
        {
            _byteFunc = byteFunc ?? throw new ArgumentNullException(nameof(byteFunc));
        }

        public byte[] GetResponse(string request)
        {
            return GetResponse((request ?? string.Empty).GetBytes());
        }

        /// <summary>
        /// Builds the response for a request. If a configured function throws, an empty response is returned.
        /// </summary>
        public byte[] GetResponse(byte[] request)
        {
            try
            {
                if (_stringFunc != null) return (_stringFunc(request.GetString()) ?? string.Empty).GetBytes();
                if (_byteFunc != null) return _byteFunc(request) ?? new byte[0];
                return _response;
            }
            catch (Exception)
            {
                return new byte[0];
            }
        }
    }
}
