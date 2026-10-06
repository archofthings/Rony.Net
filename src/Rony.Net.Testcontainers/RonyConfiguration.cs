using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;

namespace Rony.Net.Testcontainers
{
    /// <summary>The configuration of a <see cref="RonyContainer"/>, carried through the builder's <c>Clone</c> and <c>Merge</c>.</summary>
    public sealed class RonyConfiguration : ContainerConfiguration
    {
        /// <summary>Creates an empty configuration.</summary>
        public RonyConfiguration()
        {
        }

        internal RonyConfiguration(string configurationJson)
            : this()
        {
            ConfigurationJson = configurationJson;
        }

        /// <summary>Copies the values of a resource configuration.</summary>
        /// <param name="resourceConfiguration">The configuration to copy.</param>
        public RonyConfiguration(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
            : base(resourceConfiguration)
        {
        }

        /// <summary>Copies the values of a container configuration.</summary>
        /// <param name="resourceConfiguration">The configuration to copy.</param>
        public RonyConfiguration(IContainerConfiguration resourceConfiguration)
            : base(resourceConfiguration)
        {
        }

        /// <summary>Copies the values of another configuration.</summary>
        /// <param name="resourceConfiguration">The configuration to copy.</param>
        public RonyConfiguration(RonyConfiguration resourceConfiguration)
            : this(new RonyConfiguration(), resourceConfiguration)
        {
        }

        /// <summary>Combines two configurations; the values of <paramref name="newValue"/> win.</summary>
        /// <param name="oldValue">The earlier configuration.</param>
        /// <param name="newValue">The later configuration.</param>
        public RonyConfiguration(RonyConfiguration oldValue, RonyConfiguration newValue)
            : base(oldValue, newValue)
        {
            ConfigurationJson = newValue.ConfigurationJson ?? oldValue.ConfigurationJson;
        }

        /// <summary>The configuration of the mock as JSON text (format version 1), or <c>null</c> when none was set.</summary>
        public string ConfigurationJson { get; }
    }
}
