using Rony.Interfaces;
using Rony.Models;
using Rony.Wrappers;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    /// <summary>
    /// A UDP server. Every datagram is one request, and responses are sent back to the datagram's sender.
    /// The socket is bound when the server is created.
    /// </summary>
    public class UdpServer : IListener
    {
        private IPEndPoint _endPoint;
        private readonly object _syncRoot = new object();
        private UdpClientWrapper _listener;
        private readonly bool _dualMode;
        private bool _active;

        public IPAddress Address { get; set; }
        public int Port { get; set; }
        public bool Active => _active;

        /// <summary>Binds to the given endpoint. Use port 0 to let the operating system pick a free port.</summary>
        public UdpServer(IPEndPoint localEp) : this(localEp, false)
        {
        }

        /// <summary>
        /// Binds to the given endpoint; with <paramref name="dualMode"/> and an IPv6 address (typically <see cref="IPAddress.IPv6Any"/>)
        /// the socket also receives IPv4 datagrams, whose senders appear as IPv4-mapped IPv6 addresses such as <c>::ffff:127.0.0.1</c>.
        /// A restart keeps the dual mode.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="dualMode"/> is true and the address is not an IPv6 address.</exception>
        public UdpServer(IPEndPoint localEp, bool dualMode)
        {
            if (localEp == null) throw new ArgumentNullException(nameof(localEp));
            if (dualMode && localEp.AddressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentException("Dual mode needs an IPv6 address, for example IPAddress.IPv6Any.", nameof(dualMode));
            _dualMode = dualMode;
            // Bind right away so port conflicts surface here, like they always have.
            _listener = Bind(localEp);
            // With port 0 the OS picks a free port; keep it so a restart binds the same port again.
            _endPoint = new IPEndPoint(localEp.Address, ((IPEndPoint)_listener.Client.LocalEndPoint).Port);
            Address = _endPoint.Address;
            Port = _endPoint.Port;
        }

        /// <summary>Binds to all interfaces (0.0.0.0) on the given port.</summary>
        public UdpServer(int port = 3000) : this(new IPEndPoint(IPAddress.Any, port))
        {
        }

        /// <summary>Binds to the given IP address and port.</summary>
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
                _listener ??= Bind(_endPoint);
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

        private UdpClientWrapper Bind(IPEndPoint endPoint)
        {
            return _dualMode ? new UdpClientWrapper(endPoint, true) : new UdpClientWrapper(endPoint);
        }

        private UdpClientWrapper GetListener()
        {
            return _listener ?? throw new ObjectDisposedException(nameof(UdpServer));
        }
    }
}
