using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinPreroll;

public class PluginServiceRegistrar : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		serviceCollection.AddSingleton<IIntroProvider, PrerollIntroProvider>();
		serviceCollection.AddSingleton<IStartupFilter, WebPlayerInjection>();
		serviceCollection.AddHostedService<PrerollSessionWatcher>();
	}
}
