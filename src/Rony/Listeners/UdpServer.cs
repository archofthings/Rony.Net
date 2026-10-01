using Rony.Interfaces;
using Rony.Models;
using Rony.Wrappers;
using System;
using System.Net;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    public class UdpServer : IListener
    {
        private IPEndPoint _endPoint;
        private readonly object _syncRoot = new object();
        private UdpClientWrapper _listener;
        private bool _active;

        public IPAddress Address { get; set; }
        public int Port { get; set; }
        public bool Active => _active;

        public UdpServer(IPEndPoint localEp)
        {
            if (localEp == null) throw new ArgumentNullException(nameof(localEp));
            // Bind right away so port conflicts surface here, like they always have.
            _listener = new UdpClientWrapper(localEp);
            // With port 0 the OS picks a free port; keep it so a restart binds the same port again.
            _endPoint = new IPEndPoint(localEp.Address, ((IPEndPoint)_listener.Client.LocalEndPoint).Port);
            Address = _endPoint.Address;
            Port = _endPoint.Port;
        }

        public UdpServer(int port = 3000) : this(new IPEndPoint(IPAddress.Any, port))
        {
        }

        public UdpServer(string address, int port = 3000) : this(new IPEndPoint(IPAddress.Parse(address), port))
        {
        }

        public async Task<Message> ReceiveAsync()
        {
            var request = await GetListener().ReceiveAsync().ConfigureAwait(false);
            return new Message(request.Buffer, request.RemoteEndPoint, request.RemoteEndPoint);
        }

        public async Task ReplyAsync(string response, object sender)
        {
            await ReplyAsync(response.GetBytes(), sender).ConfigureAwait(false);
        }

        public async Task ReplyAsync(byte[] response, object sender)
        {
            var endPoint = (IPEndPoint)sender;
            await GetListener().SendAsync(response, response.Length, endPoint).ConfigureAwait(false);
        }

        public Task CloseAsync(object sender)
        {
            // UDP has no connection to close.
            return Task.CompletedTask;
        }

        public void Start()
        {
            lock (_syncRoot)
            {
                // The socket is released on Stop(), so re-bind when the server is restarted.
                _listener ??= new UdpClientWrapper(_endPoint);
                _active = true;
            }
        }

        public void Stop()
        {
            lock (_syncRoot)
            {
                _active = false;
                _listener?.Dispose();
                _listener = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private UdpClientWrapper GetListener()
        {
            return _listener ?? throw new ObjectDisposedException(nameof(UdpServer));
        }
    }
}
