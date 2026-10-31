using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellyfinPreroll;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public const string PluginGuid = "a4b2c3d4-e5f6-7890-abcd-ef1234567891";

	public static Plugin? Instance { get; private set; }

	public override string Name => "Pre-Roll Videos";

	public override string Description => "Play videos from a selected library (studio logos, bumpers, intros) before TV episodes and movies.";

	public override Guid Id => Guid.Parse("a4b2c3d4-e5f6-7890-abcd-ef1234567891");

	public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
		: base(applicationPaths, xmlSerializer)
	{
		Instance = this;
	}

	public IEnumerable<PluginPageInfo> GetPages()
	{
		return new PluginPageInfo[1]
		{
			new PluginPageInfo
			{
				Name = ((BasePlugin)this).Name,
				EmbeddedResourcePath = ((object)this).GetType().Namespace + ".Configuration.configPage.html"
			}
		};
	}
}
