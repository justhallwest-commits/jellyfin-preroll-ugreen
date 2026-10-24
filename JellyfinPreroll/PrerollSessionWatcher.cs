using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

/// <summary>
/// Jellyfin apps only ask the server for intros when you press Play. Episodes that
/// start by themselves (autoplay / "Up Next" / Next button), and apps that never
/// ask for intros, would get no pre-roll.
///
/// This watcher listens for playback starts. When a movie or episode starts from
/// the beginning WITHOUT a pre-roll having just played on that device, it tells the
/// device to play: pre-roll(s) → that item → the rest of the series (if autoplay is on).
///
/// Loop / double-play protection (per device session):
///   * a pre-roll clip started/stopped moments ago  → skip (pre-roll already happened)
///   * we just queued pre-rolls for this exact item → skip (our own queue reaching it)
///   * resumed playback (not near the start)        → skip
/// </summary>
public sealed class PrerollSessionWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(45);   // "from the beginning" threshold
    private static readonly TimeSpan AfterPrerollGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(10);
    private const int MaxQueuedEpisodes = 50;

    /// <summary>Clock (replaceable for testing).</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PrerollSessionWatcher> _logger;
    private readonly PrerollSelector _selector;
    private readonly ConcurrentDictionary<string, SessionState> _state = new();

    public PrerollSessionWatcher(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<PrerollSessionWatcher> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
        _selector = new PrerollSelector(libraryManager, logger);
    }

    private sealed class SessionState
    {
        public DateTime LastPrerollStartUtc { get; set; } = DateTime.MinValue;
        public DateTime LastPrerollStopUtc { get; set; } = DateTime.MinValue;
        public TimeSpan LastPrerollLength { get; set; } = TimeSpan.Zero;
        public string LastPrerollName { get; set; } = string.Empty;
        public Guid PendingItemId { get; set; } = Guid.Empty;
        public DateTime PendingSinceUtc { get; set; } = DateTime.MinValue;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("Pre-Roll Videos: autoplay watcher started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var sessionId = e.Session?.Id;
            if (sessionId is not null && _selector.IsPrerollClip(e.Item))
            {
                _state.GetOrAdd(sessionId, _ => new SessionState()).LastPrerollStopUtc = UtcNow();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-Roll Videos: stop handler error");
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            HandleStart(e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pre-Roll Videos: autoplay watcher error");
        }
    }

    private void HandleStart(PlaybackProgressEventArgs e)
    {
        var session = e.Session;
        var item = e.Item;
        if (session?.Id is null || item is null)
        {
            return;
        }

        var now = UtcNow();
        var state = _state.GetOrAdd(session.Id, _ => new SessionState());
        var device = DescribeDevice(session);

        // 1. A pre-roll clip is starting: remember it, nothing else to do.
        if (_selector.IsPrerollClip(item))
        {
            state.LastPrerollStartUtc = now;
            state.LastPrerollLength = TimeSpan.FromTicks(item.RunTimeTicks ?? 0);
            state.LastPrerollName = item.Name ?? string.Empty;
            return;
        }

        // Only movies and episodes are interesting from here on.
        if (item is not Episode && item is not Movie)
        {
            return;
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        var itemName = DescribeItem(item);

        // 2. This is the item we queued pre-rolls for — it's arriving after them. Let it play.
        if (state.PendingItemId == item.Id && now - state.PendingSinceUtc < PendingTimeout)
        {
            state.PendingItemId = Guid.Empty;
            PrerollActivityLog.Add("Playback", device, itemName, "✓ Pre-roll played first (" + state.LastPrerollName + ", added by the server fallback)", "ok");
            return;
        }

        // 3. A pre-roll just played on this device — nothing to do.
        bool prerollJustPlayed =
            now - state.LastPrerollStopUtc < AfterPrerollGrace ||
            now - state.LastPrerollStartUtc < state.LastPrerollLength + AfterPrerollGrace;
        if (prerollJustPlayed)
        {
            PrerollActivityLog.Add("Playback", device, itemName, "✓ Pre-roll played first (" + state.LastPrerollName + ")", "ok");
            return;
        }

        // 4. Only fresh starts (not resumes).
        var pos = e.PlaybackPositionTicks ?? 0;
        if (pos > StartWindow.Ticks)
        {
            PrerollActivityLog.Add("Playback", device, itemName, "Resumed at " + TimeSpan.FromTicks(pos).ToString(@"h\:mm\:ss") + " — no pre-roll on resume (by design)");
            return;
        }

        // 5. Would this item get a pre-roll at all?
        var clips = _selector.Select(item, "autoplay", true, out var reason);
        if (clips.Count == 0)
        {
            PrerollActivityLog.Add("Playback", device, itemName, "No pre-roll needed: " + reason);
            return;
        }

        if (!config.PlayBeforeAutoplay)
        {
            PrerollActivityLog.Add("Playback", device, itemName, "✗ Started without a pre-roll (server fallback is turned off)", "warn");
            return;
        }

        // 6. The device must accept remote "play" commands.
        if (!session.SupportsRemoteControl)
        {
            PrerollActivityLog.Add("Playback", device, itemName, "✗ Started without a pre-roll — this app doesn't accept remote play commands, so the server can't add one", "warn");
            return;
        }

        var queue = clips.Select(c => c.Id).ToList();
        queue.Add(item.Id);

        bool autoplayNext = e.Users?.FirstOrDefault()?.EnableNextEpisodeAutoPlay ?? true;
        if (item is Episode episode && autoplayNext)
        {
            queue.AddRange(GetFollowingEpisodes(episode));
        }

        state.PendingItemId = item.Id;
        state.PendingSinceUtc = now;

        var request = new PlayRequest
        {
            ItemIds = queue.ToArray(),
            PlayCommand = PlayCommand.PlayNow,
            StartIndex = 0
        };

        _logger.LogInformation(
            "Pre-Roll Videos: inserting {Count} pre-roll(s) before \"{Item}\" on {Device}",
            clips.Count, item.Name, device);
        PrerollActivityLog.Add(
            "Server fallback",
            device,
            itemName,
            "Started without a pre-roll → told the app to play " + string.Join(", ", clips.Select(c => c.Name)) + " first [" + reason + "]");

        _ = SendAsync(session.Id, request, itemName, device);
    }

    private static string DescribeDevice(SessionInfo session)
    {
        var d = session.DeviceName ?? string.Empty;
        var c = session.Client ?? string.Empty;
        return string.IsNullOrEmpty(c) ? d : d + " (" + c + ")";
    }

    internal static string DescribeItem(BaseItem item)
    {
        if (item is Episode ep)
        {
            var code = (ep.ParentIndexNumber.HasValue && ep.IndexNumber.HasValue)
                ? $"S{ep.ParentIndexNumber.Value:00}E{ep.IndexNumber.Value:00} "
                : string.Empty;
            var series = ep.SeriesName;
            return string.IsNullOrEmpty(series) ? code + ep.Name : series + " " + code + "· " + ep.Name;
        }

        return item.Name ?? string.Empty;
    }

    private async Task SendAsync(string sessionId, PlayRequest request, string itemName, string device)
    {
        try
        {
            await _sessionManager.SendPlayCommand(null!, sessionId, request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_state.TryGetValue(sessionId, out var s))
            {
                s.PendingItemId = Guid.Empty;
            }

            _logger.LogWarning(ex, "Pre-Roll Videos: could not send pre-roll queue for \"{Item}\"", itemName);
            PrerollActivityLog.Add("Server fallback", device, itemName, "✗ The app rejected the play command: " + ex.Message, "warn");
        }
    }

    /// <summary>Episodes after this one in the same series, in watch order (no missing/virtual ones).</summary>
    private IEnumerable<Guid> GetFollowingEpisodes(Episode current)
    {
        try
        {
            if (current.SeriesId == Guid.Empty)
            {
                return Array.Empty<Guid>();
            }

            var query = new InternalItemsQuery
            {
                AncestorIds = new[] { current.SeriesId },
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsVirtualItem = false,
                Recursive = true
            };

            bool currentIsSpecial = (current.ParentIndexNumber ?? 1) == 0;
            var ordered = _libraryManager.GetItemsResult(query).Items
                .Where(i => currentIsSpecial || (i.ParentIndexNumber ?? 1) != 0)
                .OrderBy(i => i.ParentIndexNumber ?? int.MaxValue)
                .ThenBy(i => i.IndexNumber ?? int.MaxValue)
                .ThenBy(i => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int idx = ordered.FindIndex(i => i.Id == current.Id);
            return idx < 0
                ? Array.Empty<Guid>()
                : ordered.Skip(idx + 1).Take(MaxQueuedEpisodes).Select(i => i.Id).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pre-Roll Videos: could not list following episodes");
            return Array.Empty<Guid>();
        }
    }
}
