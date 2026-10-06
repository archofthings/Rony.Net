using System;
using System.IO;
using System.Reflection;
using System.Text;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;

namespace Rony.Net.Testcontainers
{
    /// <summary>
    /// Builds a <see cref="RonyContainer"/>: the <c>rony</c> tool of Rony.Net in a Docker container, serving a configuration file.
    /// The configuration is copied into the container as <c>/config/mock.json</c> and the tool starts with
    /// <c>run /config/mock.json --address 0.0.0.0 --port 4000 --control 4001 --control-address 0.0.0.0</c>, so the address and port
    /// of the configuration are replaced (a <c>unix</c> configuration is not supported: the container exits with the tool's error).
    /// A <c>udp</c> configuration needs its own port binding, for example <c>WithPortBinding("4000/udp", true)</c>.
    /// </summary>
    public sealed class RonyBuilder : ContainerBuilder<RonyBuilder, RonyContainer, RonyConfiguration>
    {
        /// <summary>The image name; the default tag is the version of this package.</summary>
        public const string RonyImage = "ghcr.io/archofthings/rony";

        /// <summary>The port of the mock inside the container.</summary>
        public const ushort RonyPort = 4000;

        /// <summary>The port of the control endpoint inside the container.</summary>
        public const ushort ControlPort = 4001;

        private const string ConfigurationPath = "/config/mock.json";

        /// <summary>Creates a builder that uses the image <c>ghcr.io/archofthings/rony</c> at the version of this package.</summary>
        public RonyBuilder()
            : this(new RonyConfiguration())
        {
            DockerResourceConfiguration = Init().DockerResourceConfiguration;
        }

        private RonyBuilder(RonyConfiguration resourceConfiguration)
            : base(resourceConfiguration)
        {
            DockerResourceConfiguration = resourceConfiguration;
        }

        /// <inheritdoc />
        protected override RonyConfiguration DockerResourceConfiguration { get; }

        /// <summary>
        /// Uses a configuration file of the host (format version 1). The file is read now and copied into the container, so it also works
        /// with a remote Docker host. Files that the configuration names (a TLS certificate) are not copied: add each with
        /// <c>WithResourceMapping(...)</c> at the path the configuration names. The last call of this method and
        /// <see cref="WithConfiguration"/> wins.
        /// </summary>
        /// <param name="path">The path of the configuration file.</param>
        /// <returns>A configured instance of <see cref="RonyBuilder"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is <c>null</c>.</exception>
        /// <exception cref="FileNotFoundException">The file does not exist.</exception>
        public RonyBuilder WithConfigurationFile(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return WithConfiguration(File.ReadAllText(path));
        }

        /// <summary>
        /// Uses a configuration given as JSON text (format version 1); it is copied into the container. Files that the configuration
        /// names (a TLS certificate) are not copied: add each with <c>WithResourceMapping(...)</c> at the path the configuration names.
        /// The last call of this method and <see cref="WithConfigurationFile"/> wins.
        /// </summary>
        /// <param name="json">The configuration.</param>
        /// <returns>A configured instance of <see cref="RonyBuilder"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is <c>null</c>.</exception>
        public RonyBuilder WithConfiguration(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            return Merge(DockerResourceConfiguration, new RonyConfiguration(json))
                .WithResourceMapping(new UTF8Encoding(false).GetBytes(json), ConfigurationPath);   // default file mode 0644 (owner and group may write, all may read), so the image's non-root user can read it
        }

        /// <inheritdoc />
        public override RonyContainer Build()
        {
            Validate();
            return new RonyContainer(DockerResourceConfiguration);
        }

        /// <inheritdoc />
        protected override RonyBuilder Init()
        {
            return base.Init()
                .WithImage(RonyImage + ":" + PackageVersion())
                .WithCommand("run", ConfigurationPath, "--address", "0.0.0.0", "--port", "4000", "--control", "4001", "--control-address", "0.0.0.0")
                .WithPortBinding(RonyPort, true)
                .WithPortBinding(ControlPort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Control on "));
        }

        /// <inheritdoc />
        protected override void Validate()
        {
            // First, so a missing configuration is reported without asking Docker anything.
            if (DockerResourceConfiguration.ConfigurationJson == null)
            {
                throw new ArgumentException("A configuration is required: call WithConfigurationFile(...) or WithConfiguration(...).", "configuration");
            }

            base.Validate();
        }

        /// <inheritdoc />
        protected override RonyBuilder Clone(IResourceConfiguration<CreateContainerParameters> resourceConfiguration)
        {
            return Merge(DockerResourceConfiguration, new RonyConfiguration(resourceConfiguration));
        }

        /// <inheritdoc />
        protected override RonyBuilder Clone(IContainerConfiguration resourceConfiguration)
        {
            return Merge(DockerResourceConfiguration, new RonyConfiguration(resourceConfiguration));
        }

        /// <inheritdoc />
        protected override RonyBuilder Merge(RonyConfiguration oldValue, RonyConfiguration newValue)
        {
            return new RonyBuilder(new RonyConfiguration(oldValue, newValue));
        }

        // The image the container will use; for the tests, as Build() asks Docker.
        internal string ImageName => DockerResourceConfiguration.Image.FullName;

        // The informational version without the "+metadata" suffix, so the tag of the image matches the package.
        internal static string PackageVersion()
        {
            var assembly = typeof(RonyBuilder).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version.ToString(3);
            var plus = version.IndexOf('+');
            return plus < 0 ? version : version.Substring(0, plus);
        }
    }
}
