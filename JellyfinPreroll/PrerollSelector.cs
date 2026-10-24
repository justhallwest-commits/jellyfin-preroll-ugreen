using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

/// <summary>
/// Decides which pre-roll clips play before an item. Shared by the intro
/// provider (normal "Play" starts) and the session watcher (autoplay / Next).
/// </summary>
public class PrerollSelector
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly object _stateLock = new();
    private static readonly Random _rng = new();
    private static readonly Dictionary<string, HashSet<Guid>> _lastPlayed = new();
    private static readonly Dictionary<string, int> _sequentialIndex = new();

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger _logger;

    public PrerollSelector(ILibraryManager libraryManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>True when the item lives in the configured pre-roll library.</summary>
    public bool IsPrerollClip(BaseItem? item)
    {
        var config = Plugin.Instance?.Configuration;
        if (item is null || config is null || !Guid.TryParse(config.PrerollLibraryId, out var libId) || libId == Guid.Empty)
        {
            return false;
        }

        return GetLibraryIds(item).Contains(libId);
    }

    /// <summary>Pick the clips to play before <paramref name="item"/> (empty list = none).</summary>
    public List<BaseItem> Select(BaseItem? item, string trigger)
    {
        try
        {
            return SelectInternal(item, trigger);
        }
        catch (Exception ex)
        {
            // Never break playback because of a pre-roll problem.
            _logger.LogError(ex, "Pre-Roll Videos: failed to pick pre-rolls for {Item}", item?.Name);
            return new List<BaseItem>();
        }
    }

    private List<BaseItem> SelectInternal(BaseItem? item, string trigger)
    {
        var none = new List<BaseItem>();
        var config = Plugin.Instance?.Configuration;
        if (config is null || item is null)
        {
            return none;
        }

        if (!Guid.TryParse(config.PrerollLibraryId, out var prerollLibraryId) || prerollLibraryId == Guid.Empty)
        {
            _logger.LogDebug("Pre-Roll Videos: no pre-roll library selected — skipping");
            return none;
        }

        if (!IsEligibleType(item, config))
        {
            return none;
        }

        var itemLibraryIds = GetLibraryIds(item);

        // Loop prevention: never put pre-rolls in front of the pre-roll clips themselves.
        if (itemLibraryIds.Contains(prerollLibraryId))
        {
            return none;
        }

        var excludedLibs = ParseIds(config.ExcludedLibraryIds);
        if (excludedLibs.Count > 0 && itemLibraryIds.Overlaps(excludedLibs))
        {
            _logger.LogDebug("Pre-Roll Videos: \"{Item}\" is in an excluded library — skipping", item.Name);
            return none;
        }

        // Tags on the item plus its parents (season, series) and library.
        var mediaTags = GetMediaTags(item);

        var skipTags = CleanSet(config.SkipTags);
        var hit = mediaTags.FirstOrDefault(skipTags.Contains);
        if (hit is not null)
        {
            _logger.LogInformation("Pre-Roll Videos: \"{Item}\" has skip tag \"{Tag}\" — no pre-roll", item.Name, hit);
            return none;
        }

        var pool = GetPrerollVideos(prerollLibraryId, config.MaxDurationSeconds);
        if (pool.Count == 0)
        {
            _logger.LogWarning("Pre-Roll Videos: pre-roll library {Id} has no usable videos", prerollLibraryId);
            return none;
        }

        int count = Math.Clamp(config.PrerollCount, 1, 5);
        var selected = config.EnableTagMatching
            ? SelectWithTags(pool, mediaTags, count, config, out var reason)
            : Pick(pool, count, "all", config.Order, out reason);

        if (selected.Count == 0)
        {
            _logger.LogInformation("Pre-Roll Videos ({Trigger}): nothing to play before \"{Item}\" ({Reason})", trigger, item.Name, reason);
            return none;
        }

        _logger.LogInformation(
            "Pre-Roll Videos ({Trigger}): {Count} pre-roll(s) before \"{Item}\" [{Reason}]: {Clips}",
            trigger,
            selected.Count,
            item.Name,
            reason,
            string.Join(", ", selected.Select(v => v.Name)));

        return selected;
    }

    internal List<BaseItem> SelectWithTags(
        List<BaseItem> pool,
        HashSet<string> mediaTags,
        int count,
        PluginConfiguration config,
        out string reason)
    {
        // Which clip tags does this item want?
        var wanted = new HashSet<string>(Ci);
        if (config.AutoMatchSameTags)
        {
            wanted.UnionWith(mediaTags);
        }

        foreach (var rule in config.TagRules ?? Array.Empty<TagRule>())
        {
            var mediaTag = rule?.MediaTag?.Trim();
            var clipTag = rule?.PrerollTag?.Trim();
            if (!string.IsNullOrEmpty(mediaTag) && !string.IsNullOrEmpty(clipTag) && mediaTags.Contains(mediaTag))
            {
                wanted.Add(clipTag);
            }
        }

        var general = pool.Where(v => ClipTags(v).Count == 0).ToList();
        var matched = wanted.Count == 0
            ? new List<BaseItem>()
            : pool.Where(v => ClipTags(v).Overlaps(wanted)).ToList();

        if (matched.Count > 0)
        {
            var matchedTags = matched.SelectMany(ClipTags).Where(wanted.Contains).Distinct(Ci).OrderBy(t => t, Ci);
            var key = "tag:" + string.Join("|", matchedTags);
            var picks = Pick(matched, count, key, config.Order, out _);
            reason = "matched tags: " + string.Join(", ", matchedTags);

            if (picks.Count < count && config.FillWithGeneral && general.Count > 0)
            {
                var ids = picks.Select(p => p.Id).ToHashSet();
                var extra = Pick(general.Where(g => !ids.Contains(g.Id)).ToList(), count - picks.Count, "general", config.Order, out _);
                picks.AddRange(extra);
                if (extra.Count > 0)
                {
                    reason += " + general";
                }
            }

            return picks;
        }

        switch (config.WhenNoMatch)
        {
            case NoMatchBehavior.PlayNothing:
                reason = "no tag match, set to play nothing";
                return new List<BaseItem>();
            case NoMatchBehavior.PlayAny:
                reason = "no tag match, any clip";
                return Pick(pool, count, "all", config.Order, out _);
            default:
                reason = general.Count > 0 ? "no tag match, general clips" : "no tag match and no untagged clips";
                return Pick(general, count, "general", config.Order, out _);
        }
    }

    private static bool IsEligibleType(BaseItem item, PluginConfiguration config) => item switch
    {
        Episode => config.EnableForTvShows,
        Movie => config.EnableForMovies,
        _ => false
    };

    private HashSet<string> GetMediaTags(BaseItem item)
    {
        try
        {
            return CleanSet(item.GetInheritedTags());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-Roll Videos: could not read inherited tags for {Item}", item.Name);
            return CleanSet(item.Tags);
        }
    }

    private static HashSet<string> ClipTags(BaseItem clip) => CleanSet(clip.Tags);

    private static HashSet<string> CleanSet(IEnumerable<string>? values)
    {
        var set = new HashSet<string>(Ci);
        if (values is null)
        {
            return set;
        }

        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                set.Add(v.Trim());
            }
        }

        return set;
    }

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
        foreach (var s in raw ?? Array.Empty<string>())
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
            videos = videos.Where(v => v.RunTimeTicks.HasValue && v.RunTimeTicks.Value <= maxTicks).ToList();
        }

        return videos.OrderBy(v => v.SortName ?? v.Name, Ci).ToList();
    }

    /// <summary>Pick clips from a pool; each pool (key) keeps its own rotation / no-repeat memory.</summary>
    private static List<BaseItem> Pick(List<BaseItem> pool, int count, string key, PrerollOrder order, out string reason)
    {
        reason = "all clips";
        count = Math.Min(count, pool.Count);
        if (count <= 0)
        {
            return new List<BaseItem>();
        }

        lock (_stateLock)
        {
            if (order == PrerollOrder.Sequential)
            {
                _sequentialIndex.TryGetValue(key, out var start);
                var picked = new List<BaseItem>(count);
                for (int i = 0; i < count; i++)
                {
                    picked.Add(pool[(start + i) % pool.Count]);
                }

                _sequentialIndex[key] = (start + count) % pool.Count;
                return picked;
            }

            _lastPlayed.TryGetValue(key, out var last);
            last ??= new HashSet<Guid>();
            var fresh = pool.Where(v => !last.Contains(v.Id)).ToList();
            var source = fresh.Count >= count ? fresh : pool;
            var result = source.OrderBy(_ => _rng.Next()).Take(count).ToList();
            _lastPlayed[key] = result.Select(v => v.Id).ToHashSet();
            return result;
        }
    }
}
