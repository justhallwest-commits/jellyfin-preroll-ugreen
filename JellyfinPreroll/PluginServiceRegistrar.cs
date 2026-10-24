using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinPreroll;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class PluginServiceRegistrar : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Pre-rolls when Play is pressed.
        serviceCollection.AddSingleton<IIntroProvider, PrerollIntroProvider>();

        // Web player integration: adds the queue-building script to the web app.
        serviceCollection.AddSingleton<IStartupFilter, WebPlayerInjection>();

        // Server fallback for apps without the script (native TV apps, etc.).
        serviceCollection.AddHostedService<PrerollSessionWatcher>();
    }
}
