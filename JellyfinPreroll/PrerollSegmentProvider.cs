#if JF12
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

/// <summary>
/// Jellyfin 12: marks each pre-roll clip as one skippable segment (Intro, Commercial or Preview), so apps
/// that support media segments show a Skip button while it plays. Jellyfin creates segments when it scans
/// the pre-roll library (or runs Scheduled Tasks → "Media Segment Scan").
/// </summary>
public class PrerollSegmentProvider : IMediaSegmentProvider
{
    private readonly PrerollSelector _selector;
    private readonly ILibraryManager _libraryManager;

    public PrerollSegmentProvider(ILibraryManager libraryManager, ILogger<PrerollSegmentProvider> logger)
    {
        _libraryManager = libraryManager;
        _selector = new PrerollSelector(libraryManager, logger);
    }

    public string Name => "Pre-Roll Videos";

    public ValueTask<bool> Supports(BaseItem item)
    {
        var cfg = Plugin.Instance?.Configuration;
        return ValueTask.FromResult(cfg is not null && cfg.SkippablePrerolls && _selector.IsPrerollClip(item));
    }

    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Configuration;
        var item = _libraryManager.GetItemById(request.ItemId);
        if (cfg is null || !cfg.SkippablePrerolls || item is null || !item.RunTimeTicks.HasValue || item.RunTimeTicks.Value <= 0)
        {
            return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(Array.Empty<MediaSegmentDto>());
        }

        var type = cfg.SkippableType switch
        {
            "Commercial" => MediaSegmentType.Commercial,
            "Preview" => MediaSegmentType.Preview,
            _ => MediaSegmentType.Intro
        };

        IReadOnlyList<MediaSegmentDto> segments = new[]
        {
            new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = request.ItemId,
                Type = type,
                StartTicks = 0,
                // stop just short of the end so the app moves on to the movie itself
                EndTicks = Math.Max(0, item.RunTimeTicks.Value - TimeSpan.FromMilliseconds(250).Ticks)
            }
        };
        return Task.FromResult(segments);
    }

    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Jellyfin's own "Media Segment Scan" only looks at movies, episodes and audio, and pre-roll clips are often
/// home videos, so the plugin asks for segments on its clips itself: at start-up and whenever settings are saved.
/// </summary>
internal static class SkippableMarker
{
    private static int _running;

    public static async Task RunAsync(IServiceProvider services, ILibraryManager libraryManager, ILogger logger)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg is null || services.GetService(typeof(IMediaSegmentManager)) is not IMediaSegmentManager segments)
            {
                return;
            }

            var selector = new PrerollSelector(libraryManager, logger);
            var clips = selector.GetAllClips();
            var done = 0;
            foreach (var clip in clips)
            {
                if (cfg.SkippablePrerolls)
                {
                    await segments.RunSegmentPluginProviders(clip, libraryManager.GetLibraryOptions(clip), false, CancellationToken.None).ConfigureAwait(false);
                    done++;
                }
                else if (segments.HasSegments(clip.Id))
                {
                    await segments.DeleteSegmentsAsync(clip.Id, CancellationToken.None).ConfigureAwait(false);
                    done++;
                }
            }

            if (done > 0)
            {
                logger.LogInformation("Pre-Roll Videos: {Action} skip segments on {Count} clip(s)", cfg.SkippablePrerolls ? "updated" : "removed", done);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Pre-Roll Videos: couldn't update skip segments");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
#endif
