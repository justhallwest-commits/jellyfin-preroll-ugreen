using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellyfinPreroll;

/// <summary>
/// Pre-Roll Videos plugin — plays videos from a designated library
/// before TV episodes and/or movies.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Plugin GUID. Must match manifest.json and configPage.html.
    /// Kept identical to v1.2.6 so existing settings carry over.
    /// </summary>
    public const string PluginGuid = "a4b2c3d4-e5f6-7890-abcd-ef1234567891";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Singleton so other classes can read configuration.</summary>
    public static Plugin? Instance { get; private set; }

    public override string Name => "Pre-Roll Videos";

    public override string Description =>
        "Play videos from a selected library (studio logos, bumpers, intros) before TV episodes and movies.";

    public override Guid Id => Guid.Parse(PluginGuid);

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
