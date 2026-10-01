using Rony.Helpers;
using Rony.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Rony.Handlers
{
    public class RequestHandler
    {
        private static readonly byte[] AnyRequest = new byte[0];

        private readonly ConcurrentDictionary<byte[], Config> _configs;
        private byte[] _receiveData;

        /// <summary>
        /// Configured responses, keyed by the raw request bytes. An empty key matches any request.
        /// </summary>
        public IReadOnlyDictionary<byte[], Config> Configs => _configs;

        public RequestHandler()
        {
            _configs = new ConcurrentDictionary<byte[], Config>(ByteArrayComparer.Instance);
        }

        public RequestHandler Send(string receiveData)
        {
            return Send((receiveData ?? string.Empty).GetBytes());
        }

        public RequestHandler Send(byte[] receiveData)
        {
            _receiveData = receiveData ?? AnyRequest;
            return this;
        }

        public void Receive(string response)
        {
            Add(new Config(response));
        }

        public void Receive(byte[] response)
        {
            Add(new Config(response));
        }

        public void Receive(Func<string, string> func)
        {
            Add(new Config(func));
        }

        public void Receive(Func<byte[], byte[]> func)
        {
            Add(new Config(func));
        }

        public byte[] Match(string request)
        {
            return Match((request ?? string.Empty).GetBytes());
        }

        public byte[] Match(byte[] request)
        {
            request ??= AnyRequest;
            if (_configs.TryGetValue(request, out var config) || _configs.TryGetValue(AnyRequest, out config))
                return config.GetResponse(request);
            return new byte[0];
        }

        private void Add(Config config)
        {
            if (_receiveData == null)
                throw new InvalidOperationException($"Call {nameof(Send)}() before {nameof(Receive)}().");

            var request = _receiveData;
            _receiveData = null;
            if (!_configs.TryAdd(request, config))
                throw new ArgumentException($"A response is already configured for request '{request.GetString()}'.");
        }
    }
}
