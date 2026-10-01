using System;
using System.Text.RegularExpressions;

namespace Rony.Handlers
{
    /// <summary>
    /// Starts a rule that only applies in one scenario state. Returned by <see cref="RequestHandler.InState"/>.
    /// </summary>
    public sealed class StateRequestBuilder
    {
        private readonly RequestHandler _handler;
        private readonly string _state;

        internal StateRequestBuilder(RequestHandler handler, string state)
        {
            _handler = handler;
            _state = state;
        }

        /// <summary>Configures the response to this exact request in this state. An empty request matches any request.</summary>
        public RequestHandler Send(string receiveData) => _handler.SendExact((receiveData ?? string.Empty).GetBytes(), _state);

        /// <inheritdoc cref="Send(string)"/>
        public RequestHandler Send(byte[] receiveData) => _handler.SendExact(receiveData, _state);

        /// <summary>Configures the response to every request whose text matches <paramref name="pattern"/> in this state.</summary>
        public RequestHandler Send(Regex pattern) => _handler.SendPattern(pattern, _state);

        /// <summary>Configures the response to every request whose text satisfies <paramref name="predicate"/> in this state.</summary>
        public RequestHandler SendMatching(Func<string, bool> predicate) => _handler.SendText(predicate, _state);

        /// <summary>Configures the response to every request whose bytes satisfy <paramref name="predicate"/> in this state.</summary>
        public RequestHandler SendMatchingBytes(Func<byte[], bool> predicate) => _handler.SendBytes(predicate, _state);
    }
}
