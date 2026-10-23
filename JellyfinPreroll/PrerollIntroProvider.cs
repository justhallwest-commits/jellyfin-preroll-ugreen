using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

/// <summary>
/// Server-side intro provider. Jellyfin calls <see cref="GetIntros"/> when a
/// client starts an item from the beginning and asks the server for intros.
/// </summary>
public class PrerollIntroProvider : IIntroProvider
{
    private static readonly object _stateLock = new();
    private static readonly Random _rng = new();
    private static HashSet<Guid> _lastPlayed = new();
    private static int _sequentialIndex;

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PrerollIntroProvider> _logger;

    public PrerollIntroProvider(ILibraryManager libraryManager, ILogger<PrerollIntroProvider> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Pre-Roll Videos";

    /// <inheritdoc />
    public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        try
        {
            return Task.FromResult(GetIntrosInternal(item));
        }
        catch (Exception ex)
        {
            // Never break playback because of a pre-roll problem.
            _logger.LogError(ex, "Pre-Roll Videos: failed to build intros for {Item}", item?.Name);
            return Task.FromResult(Enumerable.Empty<IntroInfo>());
        }
    }

    private IEnumerable<IntroInfo> GetIntrosInternal(BaseItem item)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || item is null)
        {
            return Enumerable.Empty<IntroInfo>();
        }

        if (!Guid.TryParse(config.PrerollLibraryId, out var prerollLibraryId) || prerollLibraryId == Guid.Empty)
        {
            _logger.LogDebug("Pre-Roll Videos: no pre-roll library selected — skipping");
            return Enumerable.Empty<IntroInfo>();
        }

        if (!IsEligibleType(item, config))
        {
            return Enumerable.Empty<IntroInfo>();
        }

        // Which libraries does the item live in?
        var itemLibraryIds = GetLibraryIds(item);

        // Loop prevention: never put pre-rolls in front of something that is
        // itself inside the pre-roll library.
        if (itemLibraryIds.Contains(prerollLibraryId))
        {
            return Enumerable.Empty<IntroInfo>();
        }

        var excluded = ParseIds(config.ExcludedLibraryIds);
        if (excluded.Count > 0 && itemLibraryIds.Overlaps(excluded))
        {
            _logger.LogDebug("Pre-Roll Videos: \"{Item}\" is in an excluded library — skipping", item.Name);
            return Enumerable.Empty<IntroInfo>();
        }

        var pool = GetPrerollVideos(prerollLibraryId, config.MaxDurationSeconds);
        if (pool.Count == 0)
        {
            _logger.LogWarning("Pre-Roll Videos: pre-roll library {Id} has no usable videos", prerollLibraryId);
            return Enumerable.Empty<IntroInfo>();
        }

        int count = Math.Min(Math.Clamp(config.PrerollCount, 1, 5), pool.Count);
        var selected = config.Order == PrerollOrder.Sequential
            ? PickSequential(pool, count)
            : PickShuffle(pool, count);

        _logger.LogInformation(
            "Pre-Roll Videos: playing {Count} pre-roll(s) before \"{Item}\": {Clips}",
            selected.Count,
            item.Name,
            string.Join(", ", selected.Select(v => v.Name)));

        return selected.Select(v => new IntroInfo { ItemId = v.Id }).ToList();
    }

    private static bool IsEligibleType(BaseItem item, PluginConfiguration config) => item switch
    {
        Episode => config.EnableForTvShows,
        Movie => config.EnableForMovies,
        _ => false
    };

    private HashSet<Guid> GetLibraryIds(BaseItem item)
    {
        try
        {
            return _libraryManager.GetCollectionFolders(item).Select(f => f.Id).ToHashSet();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-Roll Videos: could not resolve libraries for {Item}", item.Name);
            return new HashSet<Guid>();
        }
    }

    private static HashSet<Guid> ParseIds(IEnumerable<string>? raw)
    {
        var set = new HashSet<Guid>();
        if (raw is null)
        {
            return set;
        }

        foreach (var s in raw)
        {
            if (Guid.TryParse(s, out var g) && g != Guid.Empty)
            {
                set.Add(g);
            }
        }

        return set;
    }

    private List<BaseItem> GetPrerollVideos(Guid libraryId, int maxDurationSeconds)
    {
        var query = new InternalItemsQuery
        {
            ParentId = libraryId,
            IncludeItemTypes = new[] { BaseItemKind.Video, BaseItemKind.Movie, BaseItemKind.MusicVideo },
            IsVirtualItem = false,
            Recursive = true
        };

        var videos = _libraryManager.GetItemsResult(query).Items.ToList();

        if (maxDurationSeconds > 0)
        {
            var maxTicks = TimeSpan.FromSeconds(maxDurationSeconds).Ticks;
            videos = videos
                .Where(v => v.RunTimeTicks.HasValue && v.RunTimeTicks.Value <= maxTicks)
                .ToList();
        }

        // Stable order so sequential rotation is predictable.
        return videos
            .OrderBy(v => v.SortName ?? v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<BaseItem> PickShuffle(List<BaseItem> pool, int count)
    {
        lock (_stateLock)
        {
            // Prefer clips that did not play last time, as long as enough remain.
            var fresh = pool.Where(v => !_lastPlayed.Contains(v.Id)).ToList();
            var source = fresh.Count >= count ? fresh : pool;

            var picked = source.OrderBy(_ => _rng.Next()).Take(count).ToList();
            _lastPlayed = picked.Select(v => v.Id).ToHashSet();
            return picked;
        }
    }

    private static List<BaseItem> PickSequential(List<BaseItem> pool, int count)
    {
        lock (_stateLock)
        {
            var picked = new List<BaseItem>(count);
            for (int i = 0; i < count; i++)
            {
                picked.Add(pool[(_sequentialIndex + i) % pool.Count]);
            }

            _sequentialIndex = (_sequentialIndex + count) % pool.Count;
            return picked;
        }
    }
}
