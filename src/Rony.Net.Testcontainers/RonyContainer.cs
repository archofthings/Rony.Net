using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;
using Rony.Models;

namespace Rony.Net.Testcontainers
{
    /// <summary>
    /// A running <c>rony</c> container. Connect the system under test to <see cref="DockerContainer.Hostname"/> and <see cref="Port"/>;
    /// the methods read what the mock received and read or set its scenario state through the control endpoint of the tool.
    /// </summary>
    public sealed class RonyContainer : DockerContainer
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        /// <summary>Creates the container; use <see cref="RonyBuilder"/>.</summary>
        /// <param name="configuration">The configuration of the container.</param>
        public RonyContainer(RonyConfiguration configuration)
            : base(configuration)
        {
        }

        /// <summary>The host port that is mapped to the mock inside the container (<see cref="RonyBuilder.RonyPort"/>).</summary>
        public ushort Port => GetMappedPublicPort(RonyBuilder.RonyPort);

        /// <summary>The host port that is mapped to the control endpoint inside the container (<see cref="RonyBuilder.ControlPort"/>).</summary>
        public ushort ControlEndpointPort => GetMappedPublicPort(RonyBuilder.ControlPort);

        /// <summary>Gets every request that the mock kept (at most 10000), oldest first.</summary>
        /// <param name="cancellationToken">Cancels the connection and the wait for the reply.</param>
        /// <returns>The received requests.</returns>
        /// <exception cref="InvalidOperationException">The control endpoint replied with an error.</exception>
        public async Task<IReadOnlyList<ReceivedRequest>> GetReceivedRequestsAsync(CancellationToken cancellationToken = default)
        {
            var reply = await SendCommandAsync("{\"command\":\"requests\"}", cancellationToken).ConfigureAwait(false);
            var requests = new List<ReceivedRequest>();
            foreach (var entry in reply["requests"].Items)
            {
                requests.Add(ToReceivedRequest(entry));
            }

            return requests;
        }

        /// <summary>Makes the mock forget the requests it kept.</summary>
        /// <param name="cancellationToken">Cancels the connection and the wait for the reply.</param>
        /// <returns>A task that completes when the mock has cleared them.</returns>
        /// <exception cref="InvalidOperationException">The control endpoint replied with an error.</exception>
        public async Task ClearReceivedRequestsAsync(CancellationToken cancellationToken = default)
        {
            await SendCommandAsync("{\"command\":\"clear\"}", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Gets the current scenario state of the mock.</summary>
        /// <param name="cancellationToken">Cancels the connection and the wait for the reply.</param>
        /// <returns>The state name.</returns>
        /// <exception cref="InvalidOperationException">The control endpoint replied with an error.</exception>
        public async Task<string> GetStateAsync(CancellationToken cancellationToken = default)
        {
            var reply = await SendCommandAsync("{\"command\":\"state\"}", cancellationToken).ConfigureAwait(false);
            return reply["state"].AsString();
        }

        /// <summary>Sets the scenario state of the mock.</summary>
        /// <param name="state">The new state name.</param>
        /// <param name="cancellationToken">Cancels the connection and the wait for the reply.</param>
        /// <returns>A task that completes when the state is set.</returns>
        /// <exception cref="ArgumentException"><paramref name="state"/> is <c>null</c> or empty.</exception>
        /// <exception cref="InvalidOperationException">The control endpoint replied with an error.</exception>
        public async Task SetStateAsync(string state, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(state))
            {
                throw new ArgumentException("The state must not be null or empty.", nameof(state));
            }

            await SendCommandAsync("{\"command\":\"state\",\"set\":" + Quote(state) + "}", cancellationToken).ConfigureAwait(false);
        }

        // One connection, one command line, one reply line.
        private async Task<JsonData> SendCommandAsync(string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var client = new TcpClient())
            using (cancellationToken.Register(() => client.Dispose()))
            {
                string line;
                try
                {
                    await client.ConnectAsync(Hostname, ControlEndpointPort).ConfigureAwait(false);
                    var stream = client.GetStream();
                    var bytes = Utf8.GetBytes(command + "\n");
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    using (var reader = new StreamReader(stream, Utf8))
                    {
                        line = await reader.ReadLineAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (line == null)
                {
                    throw new IOException("The control endpoint closed the connection without a reply.");
                }

                var reply = JsonData.Parse(line);
                if (reply["ok"].AsBoolean() != true)
                {
                    throw new InvalidOperationException(reply["error"].AsString() ?? "The control endpoint reported an error.");
                }

                return reply;
            }
        }

        // One entry of "requests" in the reply of the control endpoint.
        internal static ReceivedRequest ToReceivedRequest(JsonData entry)
        {
            var text = entry["text"].AsString();
            var body = text != null ? Utf8.GetBytes(text) : Convert.FromBase64String(entry["base64"].AsString() ?? string.Empty);
            var time = DateTimeOffset.Parse(entry["time"].AsString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            EndPoint remote = null;
            var remoteText = entry["remote"].AsString();
            var colon = remoteText?.LastIndexOf(':') ?? -1;
            if (colon > 0
                && IPAddress.TryParse(remoteText.Substring(0, colon).Trim('[', ']'), out var address)
                && int.TryParse(remoteText.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                && port <= IPEndPoint.MaxPort)
            {
                remote = new IPEndPoint(address, port);
            }

            var connection = entry["connection"].AsNumber();
            return new ReceivedRequest(body, remote, time, entry["matched"].AsBoolean() == true, connection.HasValue ? (int)connection.Value : (int?)null);
        }

        private static string Quote(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in value)
            {
                if (c == '"' || c == '\\')
                {
                    builder.Append('\\').Append(c);
                }
                else if (c < ' ')
                {
                    builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.Append('"').ToString();
        }
    }
}
