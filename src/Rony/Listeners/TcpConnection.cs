using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    /// <summary>
    /// One accepted TCP connection. It is closed when asked to, or once the client has finished sending
    /// and every request it sent has been answered.
    /// </summary>
    internal sealed class TcpConnection
    {
        private int _pendingMessages;
        private int _readCompleted;
        private int _closed;
        private readonly Action<TcpConnection> _onClosed;
        // Responses and pushed messages can be written at the same time; streams (SslStream in particular) allow one write at a time.
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        public TcpConnection(TcpClient client, Stream stream, Action<TcpConnection> onClosed)
        {
            _onClosed = onClosed;
            Client = client;
            Stream = stream;
            RemoteEndPoint = client.Client?.RemoteEndPoint;
        }

        public TcpClient Client { get; }
        public Stream Stream { get; }
        public EndPoint RemoteEndPoint { get; }
        public bool IsClosed => Volatile.Read(ref _closed) == 1;

        public async Task WriteAsync(byte[] data)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                await Stream.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void MessageQueued() => Interlocked.Increment(ref _pendingMessages);

        public void MessageHandled()
        {
            if (Interlocked.Decrement(ref _pendingMessages) <= 0 && Volatile.Read(ref _readCompleted) == 1)
                Close();
        }

        public void ReadCompleted()
        {
            Interlocked.Exchange(ref _readCompleted, 1);
            if (Volatile.Read(ref _pendingMessages) <= 0)
                Close();
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1) return;
            try
            {
                Stream.Dispose();
            }
            catch
            {
                // Already broken; nothing to release.
            }
            Client.Dispose();
            _onClosed?.Invoke(this);
        }
    }
}
