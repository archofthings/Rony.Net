using Rony.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// Responds with the result of <paramref name="func"/>, called with the regular expression match of the request,
        /// the next time the request arrives. Only for a rule started with <c>Send(Regex)</c>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="func"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The rule was not started with <c>Send(Regex)</c>.</exception>
        public ResponseBuilder ThenMatch(Func<Match, string> func)
        {
            if (func == null) throw new ArgumentNullException(nameof(func));
            if (_config.Pattern == null)
                throw new InvalidOperationException(RequestHandler.NeedsRegex(nameof(ThenMatch)));
            return Add(ResponseStep.Reply(_config.Pattern, func));
        }

        /// <summary>Closes the connection without replying the next time the request arrives.</summary>
        public ResponseBuilder ThenDisconnect() => Add(ResponseStep.CloseConnection());

        /// <summary>Aborts the connection with a TCP reset (RST) without replying the next time the request arrives. Like <see cref="ThenDisconnect"/>, but the client sees a connection reset.</summary>
        public ResponseBuilder ThenResetConnection() => Add(ResponseStep.ResetConnection());

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
        /// Aborts the connection with a TCP reset (RST) right after the previous response is written (TCP only; a listener
        /// that cannot reset closes the connection instead). A reset discards data that has not been delivered yet,
        /// so the client may not see that response.
        /// </summary>
        public ResponseBuilder AndResetConnection()
        {
            _config.LastStep.Reset = true;
            return this;
        }

        /// <summary>
        /// Sends only the first <paramref name="byteCount"/> bytes of the previous response, as it goes on the wire
        /// (after framing and earlier <c>Truncated</c>/<c>Corrupted</c> calls). Larger than the response sends all of it,
        /// 0 sends nothing. It does not close the connection; add <see cref="AndDisconnect"/> for that. TCP only.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="byteCount"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">The previous step sends no reply.</exception>
        public ResponseBuilder Truncated(int byteCount)
        {
            if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount), "The byte count can't be negative.");
            RequireReply(nameof(Truncated)).AddModifier("truncated", bytes =>
            {
                if (bytes.Length <= byteCount) return bytes;
                var cut = new byte[byteCount];
                Buffer.BlockCopy(bytes, 0, cut, 0, byteCount);
                return cut;
            });
            return this;
        }

        /// <summary>
        /// Changes the bytes of the previous response as they go on the wire (after framing and earlier
        /// <c>Truncated</c>/<c>Corrupted</c> calls). <paramref name="corrupt"/> gets a copy; null means empty. If it
        /// throws, the response is sent unmodified and the error is logged. TCP only.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="corrupt"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The previous step sends no reply.</exception>
        public ResponseBuilder Corrupted(Func<byte[], byte[]> corrupt)
        {
            if (corrupt == null) throw new ArgumentNullException(nameof(corrupt));
            RequireReply(nameof(Corrupted)).AddModifier("corrupted", corrupt);
            return this;
        }

        /// <summary>
        /// Sends the previous response in pieces of <paramref name="chunkSize"/> bytes, as it goes on the wire (after framing and
        /// <c>Truncated</c>/<c>Corrupted</c>), waiting <paramref name="delay"/> between the pieces (not before the first or after the last).
        /// Every piece is written and flushed on its own; with a zero delay the client may still read several pieces at once.
        /// <c>After</c> still delays the start, and <c>AndDisconnect</c>/<c>AndResetConnection</c> happen after the last piece.
        /// Nothing else is written to the connection between the pieces. The last call of <c>InChunks</c> or <see cref="Throttled"/>
        /// on a step wins. A listener that cannot send in chunks (UDP) sends the response whole. TCP only.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkSize"/> is zero or negative, or <paramref name="delay"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">The previous step sends no reply.</exception>
        public ResponseBuilder InChunks(int chunkSize, TimeSpan delay = default)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize), "The chunk size must be positive.");
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay), "The delay can't be negative.");
            RequireReply(nameof(InChunks)).Chunking = (chunkSize, delay, 0);
            return this;
        }

        /// <summary>
        /// Sends the previous response at about <paramref name="bytesPerSecond"/>: about ten pieces a second, each
        /// <c>bytesPerSecond / 10</c> bytes rounded down (one byte at a time for rates under 10 bytes per second), so a rate that
        /// is not a multiple of 10 comes out a little lower. Otherwise as with <see cref="InChunks"/>. The last call of <c>Throttled</c> or
        /// <c>InChunks</c> on a step wins. A listener that cannot send in chunks (UDP) sends the response whole. TCP only.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytesPerSecond"/> is zero or negative.</exception>
        /// <exception cref="InvalidOperationException">The previous step sends no reply.</exception>
        public ResponseBuilder Throttled(int bytesPerSecond)
        {
            if (bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond), "The rate must be positive.");
            var step = RequireReply(nameof(Throttled));
            step.Chunking = bytesPerSecond < 10
                ? (1, TimeSpan.FromMilliseconds(1000.0 / bytesPerSecond), bytesPerSecond)
                : (bytesPerSecond / 10, TimeSpan.FromMilliseconds(100), bytesPerSecond);
            return this;
        }

        /// <summary>Sends <paramref name="extra"/> payloads, framed like any response, right after the previous response (used by <c>MockServer.Replay</c>).</summary>
        internal void AppendFrames(IReadOnlyList<byte[]> extra, Func<byte[], byte[]> frame)
        {
            _config.LastStep.AddModifier("replayed frames", first =>
            {
                var frames = extra.Select(frame).ToList();
                var all = new byte[first.Length + frames.Sum(f => f.Length)];
                Buffer.BlockCopy(first, 0, all, 0, first.Length);
                var offset = first.Length;
                foreach (var f in frames)
                {
                    Buffer.BlockCopy(f, 0, all, offset, f.Length);
                    offset += f.Length;
                }
                return all;
            });
        }

        private ResponseStep RequireReply(string method)
        {
            var step = _config.LastStep;
            if (!step.SendsReply)
                throw new InvalidOperationException($"{method}() changes a response, but the previous step sends no reply (Disconnect, NoReply or ResetConnection).");
            return step;
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
