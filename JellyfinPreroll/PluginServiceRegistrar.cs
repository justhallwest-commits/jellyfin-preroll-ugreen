using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinPreroll;

public class PluginServiceRegistrar : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		serviceCollection.AddSingleton<IIntroProvider, PrerollIntroProvider>();
		serviceCollection.AddSingleton<IStartupFilter, WebPlayerInjection>();
		serviceCollection.AddHostedService<PrerollSessionWatcher>();

		// Fire TV / Android TV app: pre-rolls in the episode queue it builds itself.
		serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<NativeQueueFilter>());
#if JF12
		serviceCollection.AddSingleton<MediaBrowser.Controller.MediaSegments.IMediaSegmentProvider, PrerollSegmentProvider>();
#endif
	}
}
