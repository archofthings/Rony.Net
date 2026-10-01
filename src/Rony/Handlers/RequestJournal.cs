using Rony.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Handlers
{
    /// <summary>
    /// Thread-safe record of received requests that can be awaited.
    /// </summary>
    internal sealed class RequestJournal
    {
        private readonly object _syncRoot = new object();
        private readonly List<ReceivedRequest> _requests = new List<ReceivedRequest>();
        private TaskCompletionSource<bool> _changed = NewSignal();

        public IReadOnlyList<ReceivedRequest> Snapshot()
        {
            lock (_syncRoot)
                return _requests.ToArray();
        }

        public void Record(ReceivedRequest request)
        {
            TaskCompletionSource<bool> changed;
            lock (_syncRoot)
            {
                _requests.Add(request);
                changed = _changed;
                _changed = NewSignal();
            }

            changed.TrySetResult(true);
        }

        public void Clear()
        {
            lock (_syncRoot)
                _requests.Clear();
        }

        /// <summary>
        /// Waits until <paramref name="condition"/> returns a result for the recorded requests.
        /// </summary>
        public async Task<T> WaitAsync<T>(Func<IReadOnlyList<ReceivedRequest>, T> condition, TimeSpan timeout,
            Func<IReadOnlyList<ReceivedRequest>, string> timeoutMessage, CancellationToken cancellationToken)
            where T : class
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan)
                timeoutSource.CancelAfter(timeout);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = timeoutSource.Token.Register(() => cancelled.TrySetResult(true));

            while (true)
            {
                Task changed;
                IReadOnlyList<ReceivedRequest> requests;
                lock (_syncRoot)
                {
                    requests = _requests.ToArray();
                    changed = _changed.Task;
                }

                var result = condition(requests);
                if (result != null) return result;

                if (await Task.WhenAny(changed, cancelled.Task).ConfigureAwait(false) != changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(timeoutMessage(Snapshot()));
                }
            }
        }

        public static string Describe(IReadOnlyList<ReceivedRequest> requests)
        {
            if (requests.Count == 0) return "No requests were received.";
            return "Received requests:" + Environment.NewLine +
                   string.Join(Environment.NewLine, requests.Select((r, i) => $"  {i + 1}. {r}"));
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
