using Rony.Models;
using System;

namespace Rony.Handlers
{
    /// <summary>
    /// Fluent configuration of the response(s) to one request.
    /// </summary>
    public sealed class ResponseBuilder
    {
        private readonly Config _config;

        internal ResponseBuilder(Config config)
        {
            _config = config;
        }

        /// <summary>Responds with <paramref name="response"/> the next time the request arrives.</summary>
        public ResponseBuilder Then(string response) => Add(ResponseStep.Reply(response));

        /// <inheritdoc cref="Then(string)"/>
        public ResponseBuilder Then(byte[] response) => Add(ResponseStep.Reply(response));

        /// <inheritdoc cref="Then(string)"/>
        public ResponseBuilder Then(Func<string, string> func) => Add(ResponseStep.Reply(func));

        /// <inheritdoc cref="Then(string)"/>
        public ResponseBuilder Then(Func<byte[], byte[]> func) => Add(ResponseStep.Reply(func));

        /// <summary>Closes the connection without replying the next time the request arrives.</summary>
        public ResponseBuilder ThenDisconnect() => Add(ResponseStep.CloseConnection());

        /// <summary>Stays silent the next time the request arrives.</summary>
        public ResponseBuilder ThenNoReply() => Add(ResponseStep.NoReply());

        /// <summary>Waits <paramref name="delay"/> before the previous response is sent.</summary>
        public ResponseBuilder After(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay), "The delay can't be negative.");
            _config.LastStep.Delay = delay;
            return this;
        }

        /// <summary>Closes the connection right after the previous response is sent (TCP only).</summary>
        public ResponseBuilder AndDisconnect()
        {
            _config.LastStep.Disconnect = true;
            return this;
        }

        /// <summary>
        /// Moves the scenario to <paramref name="state"/> once the previous response is used, so rules configured with
        /// <c>InState(state)</c> apply to the requests that follow.
        /// </summary>
        public ResponseBuilder GoTo(string state)
        {
            _config.LastStep.NextState = state ?? throw new ArgumentNullException(nameof(state));
            return this;
        }

        private ResponseBuilder Add(ResponseStep step)
        {
            _config.AddStep(step);
            return this;
        }
    }
}
