using Rony.Interfaces;
using Rony.Models;
using Rony.Wrappers;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Rony.Listeners
{
    public class TcpServer : IListener
    {
        private readonly TcpListenerWrapper _listener;

        public IPAddress Address { get; set; }
        public int Port { get; set; }
        public bool Active => _listener.Active;

        public TcpServer(IPAddress address, int port = 3000)
        {
            _listener = new TcpListenerWrapper(address, port);
            Address = address;
            Port = port;
        }

        public TcpServer(int port = 3000) : this(IPAddress.Loopback, port)
        {
        }

        public TcpServer(string address, int port = 3000) : this(IPAddress.Parse(address), port)
        {
        }

        public async Task<Message> ReceiveAsync()
        {
            var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[client.ReceiveBufferSize];
                using var body = new MemoryStream();
                do
                {
                    var readBytes = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (readBytes == 0) break;
                    body.Write(buffer, 0, readBytes);
                } while (stream.DataAvailable);

                return new Message(body.ToArray(), stream);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async Task ReplyAsync(string response, object sender)
        {
            await ReplyAsync(response.GetBytes(), sender).ConfigureAwait(false);
        }

        public async Task ReplyAsync(byte[] response, object sender)
        {
            using var stream = (NetworkStream)sender;
            if (response.Length > 0)
                await stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
        }

        public void Start()
        {
            _listener.Start();
        }

        public void Stop()
        {
            _listener.Stop();
        }

        public void Dispose()
        {
            _listener.Stop();
        }
    }
}
