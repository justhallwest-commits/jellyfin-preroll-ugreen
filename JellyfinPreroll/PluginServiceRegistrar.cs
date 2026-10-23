using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace JellyfinPreroll;

/// <summary>
/// Registers <see cref="PrerollIntroProvider"/> with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrar : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IIntroProvider, PrerollIntroProvider>();
    }
}
