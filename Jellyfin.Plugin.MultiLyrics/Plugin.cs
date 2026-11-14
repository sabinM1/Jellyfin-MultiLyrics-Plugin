using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MultiLyrics.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MultiLyrics;

/// <summary>
/// Main plugin class. Registers the embedded configuration page and exposes
/// well-known paths used by the result cache.
/// </summary>
public class MultiLyricsPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MultiLyricsPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server application paths.</param>
    /// <param name="xmlSerializer">Server XML serializer used to persist configuration.</param>
    public MultiLyricsPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => Constants.PluginName;

    /// <inheritdoc />
    public override string Description => "Fetches lyrics from multiple sources in a configurable priority order, reports sync status, keeps the result in memory for the current track and supports manual per-track source selection.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(Constants.PluginGuid);

    /// <summary>
    /// Gets the current plugin instance, or null before initialization.
    /// </summary>
    public static MultiLyricsPlugin? Instance { get; private set; }

    /// <summary>
    /// Gets the directory that holds the plugin's persistent cache files.
    /// Created on demand.
    /// </summary>
    public string CacheDirectory
    {
        get
        {
            var dir = Path.Combine(ApplicationPaths.PluginConfigurationsPath, "Multi-Lyrics");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Gets the current plugin configuration (null-safe static accessor for services).
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
