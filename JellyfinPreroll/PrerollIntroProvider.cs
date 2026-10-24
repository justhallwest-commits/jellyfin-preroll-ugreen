using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
#if JF1010
using Jellyfin.Data.Entities;                     // Jellyfin 10.10.x
#else
using Jellyfin.Database.Implementations.Entities; // Jellyfin 10.11.x
#endif
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

/// <summary>
/// Jellyfin calls <see cref="GetIntros"/> when a client presses Play on an item
/// from the beginning. (Autoplayed "next episodes" don't call this — see
/// <see cref="PrerollSessionWatcher"/> for that case.)
/// </summary>
public class PrerollIntroProvider : IIntroProvider
{
    private readonly PrerollSelector _selector;

    public PrerollIntroProvider(ILibraryManager libraryManager, ILogger<PrerollIntroProvider> logger)
    {
        _selector = new PrerollSelector(libraryManager, logger);
    }

    /// <inheritdoc />
    public string Name => "Pre-Roll Videos";

    /// <inheritdoc />
    public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        var clips = _selector.Select(item, "play", false, out var reason);
        if (item is MediaBrowser.Controller.Entities.TV.Episode || item is MediaBrowser.Controller.Entities.Movies.Movie)
        {
            PrerollActivityLog.Add(
                "App asked for intros",
                string.Empty,
                PrerollSessionWatcher.DescribeItem(item),
                clips.Count > 0 ? "Served " + string.Join(", ", clips.Select(c => c.Name)) + " [" + reason + "]" : "No pre-roll: " + reason,
                clips.Count > 0 ? "ok" : "info");
        }

        IEnumerable<IntroInfo> intros = clips.Select(v => new IntroInfo { ItemId = v.Id }).ToList();
        return Task.FromResult(intros);
    }
}
