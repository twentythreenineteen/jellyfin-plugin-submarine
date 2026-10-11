using System.Globalization;
using Jellyfin.Plugin.Submarine.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Submarine;

/// <summary>
/// Main entry point for the Submarine Jellyfin Plugin.
/// Provides Plex-style on-the-fly subtitle search, download, and multi-format conversion
/// across all Jellyfin platforms.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The unique plugin identifier for Submarine.
    /// </summary>
    public static readonly Guid PluginGuid = Guid.Parse("8b6a2e4c-9f1d-4e3a-b8c7-1d2e3f4a5b6c");

    private PluginConfiguration? _customConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class for testing purposes.
    /// </summary>
    /// <param name="configuration">Test configuration instance.</param>
    internal Plugin(PluginConfiguration configuration)
        : base(new DummyApplicationPaths(), new DummyXmlSerializer())
    {
        _customConfiguration = configuration;
        Instance = this;
    }

    /// <summary>
    /// Gets the current active singleton instance of the <see cref="Plugin"/>.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Submarine";

    /// <inheritdoc />
    public override Guid Id => PluginGuid;

    /// <inheritdoc />
    public override string Description =>
        "Plex-style on-the-fly subtitle search, download, and multi-format conversion in the playback subtitle menu across all platforms.";

    /// <summary>
    /// Gets the active plugin configuration, supporting test overrides when running outside a full Jellyfin host.
    /// </summary>
    public virtual PluginConfiguration CurrentConfiguration => _customConfiguration ?? Configuration;

    /// <summary>
    /// Sets a test instance of the plugin for unit testing.
    /// </summary>
    /// <param name="plugin">The plugin instance to set.</param>
    internal static void SetTestInstance(Plugin? plugin)
    {
        Instance = plugin;
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }

    private sealed class DummyApplicationPaths : IApplicationPaths
    {
        public string ProgramDataPath => Path.GetTempPath();
        public string WebPath => Path.GetTempPath();
        public string ProgramSystemPath => Path.GetTempPath();
        public string DataPath => Path.GetTempPath();
        public string ImageCachePath => Path.GetTempPath();
        public string PluginsPath => Path.GetTempPath();
        public string PluginConfigurationsPath => Path.GetTempPath();
        public string LogDirectoryPath => Path.GetTempPath();
        public string ConfigurationDirectoryPath => Path.GetTempPath();
        public string SystemConfigurationFilePath => Path.Combine(Path.GetTempPath(), "system.xml");
        public string CachePath => Path.GetTempPath();
        public string TempDirectory => Path.GetTempPath();
        public string VirtualDataPath => Path.GetTempPath();
    }

    private sealed class DummyXmlSerializer : IXmlSerializer
    {
        public void SerializeToStream(object obj, Stream stream)
        {
        }

        public void SerializeToFile(object obj, string file)
        {
        }

        public object DeserializeFromFile(Type type, string file) => new PluginConfiguration();

        public object DeserializeFromStream(Type type, Stream stream) => new PluginConfiguration();

        public object DeserializeFromBytes(Type type, byte[] buffer) => new PluginConfiguration();
    }
}
