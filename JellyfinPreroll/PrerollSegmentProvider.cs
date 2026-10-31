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
#endif
