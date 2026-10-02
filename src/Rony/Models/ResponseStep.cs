using System;

namespace Rony.Models
{
    /// <summary>
    /// One configured reaction to a request: what to send, how long to wait first, and whether to disconnect.
    /// </summary>
    internal sealed class ResponseStep
    {
        private static readonly byte[] Empty = new byte[0];

        private ResponseStep(Func<byte[], byte[]> producer, bool disconnect)
        {
            Producer = producer;
            Disconnect = disconnect;
        }

        /// <summary>Builds the reply; null means nothing is sent.</summary>
        public Func<byte[], byte[]> Producer { get; }
        public TimeSpan Delay { get; set; }
        public bool Disconnect { get; set; }

        /// <summary>The scenario state to move to once this step is used; null keeps the current state.</summary>
        public string NextState { get; set; }
        public bool SendsReply => Producer != null;

        public static ResponseStep Reply(byte[] response)
        {
            var copy = response ?? Empty;
            return new ResponseStep(_ => copy, false);
        }

        public static ResponseStep Reply(string response) => Reply((response ?? string.Empty).GetBytes());

        public static ResponseStep Reply(Func<string, string> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));
            return new ResponseStep(request => (func(request.GetString()) ?? string.Empty).GetBytes(), false);
        }

        public static ResponseStep Reply(Func<byte[], byte[]> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));
            return new ResponseStep(func, false);
        }

        public static ResponseStep NoReply() => new ResponseStep(null, false);

        public static ResponseStep CloseConnection() => new ResponseStep(null, true);

        /// <summary>
        /// Builds the reply for a request. If the configured function throws, <paramref name="onError"/> is told
        /// and an empty reply is returned.
        /// </summary>
        public byte[] Produce(byte[] request, Action<Exception> onError = null)
        {
            if (Producer == null) return Empty;
            try
            {
                return Producer(request ?? Empty) ?? Empty;
            }
            catch (Exception exception)
            {
                onError?.Invoke(exception);
                return Empty;
            }
        }
    }
}
