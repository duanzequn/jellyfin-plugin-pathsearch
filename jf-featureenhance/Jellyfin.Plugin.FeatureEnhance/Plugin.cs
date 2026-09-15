using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

[assembly: System.Runtime.InteropServices.Guid("5b3f0c47-9e21-4a8d-9c6b-7d1f4e2a8b93")]

namespace Jellyfin.Plugin.FeatureEnhance;

/// <summary>
/// Plugin entry point.
/// </summary>
public class Plugin : BasePlugin<BasePluginConfiguration>, IHasEmbeddedImage
{
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
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Feature Enhance";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5b3f0c47-9e21-4a8d-9c6b-7d1f4e2a8b93");

    /// <summary>
    /// Gets the plugin image embedded in this assembly (docs/icon.png, 512x512 square).
    /// Serving it from the assembly means a manually installed DLL still shows its icon
    /// in the dashboard without a side-car icon.png.
    /// </summary>
    public string ImageResourceName => "Jellyfin.Plugin.FeatureEnhance.icon.png";

    /// <inheritdoc />
    public override string Description =>
        "Client-side quality-of-life pack for jellyfin-web, injected at request time — no file inside the "
        + "container is ever patched. Search becomes four category tiles; clicking one opens Jellyfin's own "
        + "list page with real paging, its native sort menu, filters and play-all, and the stock search "
        + "requests are answered locally (a search no longer fetches 800 items and builds 900 cards). "
        + "Searching also matches file paths and folder names, folder results can play or shuffle the files "
        + "inside them, video cards get a 3x3 contact sheet preview built from Jellyfin's own trickplay "
        + "encoding arguments, and page navigation cross-fades instead of flashing.";
}
