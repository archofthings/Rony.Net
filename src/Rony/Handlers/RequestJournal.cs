using Rony.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Handlers
{
    /// <summary>
    /// Thread-safe, ordered record of things that happened (requests, connections) that can be awaited.
    /// </summary>
    internal sealed class Journal<T>
    {
        private readonly object _syncRoot = new object();
        private readonly List<T> _items = new List<T>();
        private TaskCompletionSource<bool> _changed = NewSignal();

        public IReadOnlyList<T> Snapshot()
        {
            lock (_syncRoot)
                return _items.ToArray();
        }

        public void Record(T item) => Record(item, 0, null);

        /// <summary>
        /// Records an item, then drops the oldest items that <paramref name="canDrop"/> accepts while more than
        /// <paramref name="max"/> are kept (0 = no limit; null = every item can be dropped). Returns the dropped items.
        /// </summary>
        public IReadOnlyList<T> Record(T item, int max, Func<T, bool> canDrop)
        {
            IReadOnlyList<T> dropped;
            lock (_syncRoot)
            {
                _items.Add(item);
                dropped = TrimCore(max, canDrop);
            }
            NotifyChanged();
            return dropped;
        }

        /// <summary>Drops the oldest items like <see cref="Record(T, int, Func{T, bool})"/>, without recording one.</summary>
        public IReadOnlyList<T> Trim(int max, Func<T, bool> canDrop)
        {
            IReadOnlyList<T> dropped;
            lock (_syncRoot)
                dropped = TrimCore(max, canDrop);
            if (dropped.Count > 0) NotifyChanged();
            return dropped;
        }

        private IReadOnlyList<T> TrimCore(int max, Func<T, bool> canDrop)
        {
            if (max <= 0 || _items.Count <= max) return Array.Empty<T>();
            var excess = _items.Count - max;
            if (canDrop == null)
            {
                var oldest = _items.GetRange(0, excess);
                _items.RemoveRange(0, excess);
                return oldest;
            }

            // One pass: compact the kept items to the front, collecting the dropped ones.
            List<T> dropped = null;
            var write = 0;
            for (var read = 0; read < _items.Count; read++)
            {
                var item = _items[read];
                if (excess > 0 && canDrop(item))
                {
                    (dropped ??= new List<T>()).Add(item);
                    excess--;
                }
                else
                {
                    _items[write++] = item;
                }
            }

            if (dropped == null) return Array.Empty<T>();
            _items.RemoveRange(write, _items.Count - write);
            return dropped;
        }

        /// <summary>Wakes up the waiters, for example after a recorded item changed.</summary>
        public void NotifyChanged()
        {
            TaskCompletionSource<bool> changed;
            lock (_syncRoot)
            {
                changed = _changed;
                _changed = NewSignal();
            }

            changed.TrySetResult(true);
        }

        public void Clear()
        {
            lock (_syncRoot)
                _items.Clear();
        }

        /// <summary>
        /// Waits until <paramref name="condition"/> returns a result for the recorded items.
        /// </summary>
        public async Task<TResult> WaitAsync<TResult>(Func<IReadOnlyList<T>, TResult> condition, TimeSpan timeout,
            Func<IReadOnlyList<T>, string> timeoutMessage, CancellationToken cancellationToken)
            where TResult : class
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan)
                timeoutSource.CancelAfter(timeout);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = timeoutSource.Token.Register(() => cancelled.TrySetResult(true));

            while (true)
            {
                Task changed;
                IReadOnlyList<T> items;
                lock (_syncRoot)
                {
                    items = _items.ToArray();
                    changed = _changed.Task;
                }

                var result = condition(items);
                if (result != null) return result;

                if (await Task.WhenAny(changed, cancelled.Task).ConfigureAwait(false) != changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(timeoutMessage(Snapshot()));
                }
            }
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static class RequestJournal
    {
        public static string Describe(IReadOnlyList<ReceivedRequest> requests)
        {
            if (requests.Count == 0) return "No requests were received.";
            return "Received requests:" + Environment.NewLine +
                   string.Join(Environment.NewLine, requests.Select((r, i) => $"  {i + 1}. {r}"));
        }

        public static string Describe(IReadOnlyList<ClientConnection> connections)
        {
            if (connections.Count == 0) return "No connections were accepted.";
            return "Connections:" + Environment.NewLine +
                   string.Join(Environment.NewLine, connections.Select(c => $"  {c}"));
        }
    }
}
