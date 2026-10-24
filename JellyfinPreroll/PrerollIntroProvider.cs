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
        IEnumerable<IntroInfo> intros = _selector.Select(item, "play")
            .Select(v => new IntroInfo { ItemId = v.Id })
            .ToList();
        return Task.FromResult(intros);
    }
}
