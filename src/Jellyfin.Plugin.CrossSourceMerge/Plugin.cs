using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.CrossSourceMerge;

/// <summary>
/// Plugin entry point. Holds the configuration and the configuration page.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Lift the source list to the new model (one row = one folder).
        // The change is in the running object immediately; it is written
        // with the next save of the configuration page.
        Engine.Umzug.Anwenden(Configuration);
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Cross-Source Merge";

    /// <summary>
    /// Gets the file name of the settings. It stays the old one, so a
    /// renamed version does not start with default values and write into
    /// foreign trees.
    /// </summary>
    public override string ConfigurationFileName => "Cross-Source Merge.xml";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("6f1c8b52-7d43-4a6e-9c31-2b5e0d7a4f18");

    /// <inheritdoc />
    public override string Description =>
        "Builds one shared tree from several source folders: one folder per title, "
        + "one link per source. Titles and TMDB ids come from the library, "
        + "only directories are configurable.";

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
}
