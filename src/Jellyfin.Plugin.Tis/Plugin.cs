using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Tis.Configuration;
using Jellyfin.Plugin.Tis.Seasons;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Tis;

/// <summary>
/// TIS – Titles In Season. Shows a library only during the part of the year it belongs to.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin's unique id. Must match the "guid" in manifest.json.
    /// </summary>
    public const string PluginGuid = "78a1b40d-66ab-4e61-a42d-62d92f23df0b";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    /// <param name="switcher">Season switcher.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, SeasonSwitcher switcher)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Saving the settings page applies the seasons straight away.
        ConfigurationChanged += (_, _) => switcher.Apply();
    }

    /// <summary>
    /// Gets the running plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "TIS - Titles In Season";

    /// <inheritdoc />
    public override string Description => "Shows a library only during its season, e.g. Halloween movies in October, and hides it for the rest of the year.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "Tis",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        };
    }
}
