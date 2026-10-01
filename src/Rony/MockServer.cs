using Rony.Handlers;
using Rony.Interfaces;
using Rony.Models;
using System;
using System.Net;
using System.Threading.Tasks;

namespace Rony.Net
{
    public class MockServer : IDisposable
    {
        private readonly IListener _listener;
        private readonly object _syncRoot = new object();
        private volatile bool _listening;

        public IPAddress Address => _listener.Address;
        public int Port => _listener.Port;
        public bool Active => _listener.Active;
        public RequestHandler Mock { get; set; }

        public MockServer(IListener listener)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            Mock = new RequestHandler();
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                if (_listening) return;
                _listener.Start();
                _listening = true;
            }

            Task.Run(ListenAsync);
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                if (!_listening) return;
                _listening = false;
                _listener.Stop();
            }
        }

        public void Dispose()
        {
            Stop();
            _listener.Dispose();
        }

        private async Task ListenAsync()
        {
            while (_listening)
            {
                Message received;
                try
                {
                    received = await _listener.ReceiveAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    // A single misbehaving client (aborted connection, failed TLS handshake, ...)
                    // must not take the whole server down.
                    continue;
                }

                _ = ReplyAsync(received);
            }
        }

        private async Task ReplyAsync(Message received)
        {
            try
            {
                var response = Mock.Match(received.Body);
                await _listener.ReplyAsync(response, received.Sender).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The client may already be gone; nothing to do.
            }
        }
    }
}
