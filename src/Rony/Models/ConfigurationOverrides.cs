using System;
using System.Net;

namespace Rony.Models
{
    /// <summary>Values that replace what a configuration says, for <c>MockServer.FromFile</c> and <c>MockServer.FromJson</c>.</summary>
    public sealed class ConfigurationOverrides
    {
        private int? _port;

        /// <summary>The address to listen on instead of <c>server.address</c>; null (the default) keeps the one of the configuration. Not allowed for the transport <c>unix</c>.</summary>
        public IPAddress Address { get; set; }

        /// <summary>The port to listen on instead of <c>server.port</c> (0 is a free port); null (the default) keeps the one of the configuration. Not allowed for the transport <c>unix</c>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not from 0 to 65535.</exception>
        public int? Port
        {
            get => _port;
            set
            {
                if (value != null && (value < 0 || value > 65535))
                    throw new ArgumentOutOfRangeException(nameof(value), "The port must be from 0 to 65535.");
                _port = value;
            }
        }
    }
}
