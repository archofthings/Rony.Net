using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Helpers
{
    /// <summary>
    /// A minimal unbounded producer/consumer queue that can be awaited.
    /// </summary>
    internal sealed class AsyncQueue<T>
    {
        private readonly ConcurrentQueue<T> _items = new ConcurrentQueue<T>();
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);

        public void Enqueue(T item)
        {
            _items.Enqueue(item);
            _available.Release();
        }

        public async Task<T> DequeueAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (_items.TryDequeue(out var item))
                    return item;
            }
        }

        public void Clear()
        {
            while (_items.TryDequeue(out _))
            {
            }
        }
    }
}
