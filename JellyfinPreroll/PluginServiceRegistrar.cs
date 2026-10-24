using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
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

        // Pre-rolls for autoplayed next episodes / apps that don't request intros.
        serviceCollection.AddHostedService<PrerollSessionWatcher>();
    }
}
