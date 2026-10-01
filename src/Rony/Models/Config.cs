using System;
using System.Collections.Generic;

namespace Rony.Models
{
    /// <summary>
    /// The configured responses for one request. With several responses (see <c>Then(...)</c>) they are used
    /// in order, and the last one keeps being used once the sequence is exhausted.
    /// </summary>
    public class Config
    {
        private readonly object _syncRoot = new object();
        private readonly List<ResponseStep> _steps = new List<ResponseStep>();
        private int _callCount;

        public Config(string response) : this(ResponseStep.Reply(response))
        {
        }

        public Config(byte[] response) : this(ResponseStep.Reply(response))
        {
        }

        public Config(Func<string, string> stringFunc) : this(ResponseStep.Reply(stringFunc))
        {
        }

        public Config(Func<byte[], byte[]> byteFunc) : this(ResponseStep.Reply(byteFunc))
        {
        }

        internal Config(ResponseStep step)
        {
            _steps.Add(step);
        }

        /// <summary>
        /// How many times this config has been used to answer a request.
        /// </summary>
        public int CallCount
        {
            get
            {
                lock (_syncRoot)
                    return _callCount;
            }
        }

        public byte[] GetResponse(string request)
        {
            return GetResponse((request ?? string.Empty).GetBytes());
        }

        /// <summary>
        /// Builds the next response for a request. If a configured function throws, an empty response is returned.
        /// </summary>
        public byte[] GetResponse(byte[] request)
        {
            return NextStep().Produce(request);
        }

        /// <summary>How the request is described in logs and messages, for example <c>"PING"</c> or <c>/^LOGIN/</c>.</summary>
        internal string Description { get; set; }

        internal ResponseStep LastStep
        {
            get
            {
                lock (_syncRoot)
                    return _steps[_steps.Count - 1];
            }
        }

        internal void AddStep(ResponseStep step)
        {
            lock (_syncRoot)
                _steps.Add(step);
        }

        internal ResponseStep NextStep()
        {
            lock (_syncRoot)
            {
                var step = _steps[Math.Min(_callCount, _steps.Count - 1)];
                _callCount++;
                return step;
            }
        }
    }
}
