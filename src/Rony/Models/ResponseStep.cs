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

        /// <summary>Abort the connection with a TCP RST instead of closing it cleanly.</summary>
        public bool Reset { get; set; }

        private readonly object _modifierLock = new object();
        private (string Kind, Func<byte[], byte[]> Apply)[] _modifiers = new (string, Func<byte[], byte[]>)[0];

        /// <summary>Changes of the framed response (truncated, corrupted), in the order they were configured.</summary>
        public (string Kind, Func<byte[], byte[]> Apply)[] Modifiers
        {
            get
            {
                lock (_modifierLock)
                    return _modifiers;
            }
        }

        public void AddModifier(string kind, Func<byte[], byte[]> apply)
        {
            lock (_modifierLock)
            {
                var copy = new (string, Func<byte[], byte[]>)[_modifiers.Length + 1];
                Array.Copy(_modifiers, copy, _modifiers.Length);
                copy[_modifiers.Length] = (kind, apply);
                _modifiers = copy;
            }
        }

        private readonly object _chunkLock = new object();
        private (int Size, TimeSpan Delay, int BytesPerSecond) _chunking;

        /// <summary>Sends the framed response in pieces: size in bytes (0 = whole), wait between pieces, and the throttle rate for logging (0 = plain chunks).</summary>
        public (int Size, TimeSpan Delay, int BytesPerSecond) Chunking
        {
            get
            {
                lock (_chunkLock)
                    return _chunking;
            }
            set
            {
                lock (_chunkLock)
                    _chunking = value;
            }
        }

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

        public static ResponseStep ResetConnection() => new ResponseStep(null, false) { Reset = true };

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
