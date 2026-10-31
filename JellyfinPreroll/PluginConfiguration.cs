using System;
using MediaBrowser.Model.Plugins;

namespace JellyfinPreroll;

public class PluginConfiguration : BasePluginConfiguration
{
	public string PrerollLibraryId { get; set; } = string.Empty;

	public int PrerollCount { get; set; } = 1;

	public bool EnableForTvShows { get; set; } = true;

	public bool EnableForMovies { get; set; } = true;

	public int MaxDurationSeconds { get; set; }

	public PrerollOrder Order { get; set; }

	public string[] DisabledClipIds { get; set; } = Array.Empty<string>();

	public bool PlayBeforeAutoplay { get; set; } = true;

	public bool EnableWebPlayerIntegration { get; set; } = true;

	public string[] ExcludedLibraryIds { get; set; } = Array.Empty<string>();

	public string[] SkipUserIds { get; set; } = Array.Empty<string>();

	public int CooldownMinutes { get; set; }

	public bool EnableTagMatching { get; set; } = true;

	public bool AutoMatchSameTags { get; set; } = true;

	public bool AutoMatchGenres { get; set; } = true;

	public bool AutoMatchStudios { get; set; }

	public bool AutoMatchRatings { get; set; }

	public MatchRule[] MatchRules { get; set; } = Array.Empty<MatchRule>();

	public TagRule[] TagRules { get; set; } = Array.Empty<TagRule>();

	public bool FillWithGeneral { get; set; } = true;

	public NoMatchBehavior WhenNoMatch { get; set; }

	public string[] SkipTags { get; set; } = Array.Empty<string>();

	public PrerollSchedule[] Schedules { get; set; } = Array.Empty<PrerollSchedule>();

	public string TimeZoneId { get; set; } = string.Empty;

	public SequenceSlot[] Sequence { get; set; } = Array.Empty<SequenceSlot>();

	// ---- Jellyfin 12 options

	/// <summary>When nothing matches, look at titles Jellyfin finds similar and use their tags/genres (Jellyfin 12).</summary>
	public bool SimilarFallback { get; set; }

	/// <summary>Alternate versions (1080p / 4K) use the main version's tags, so every version gets the same pre-roll (Jellyfin 12).</summary>
	public bool SameForAllVersions { get; set; } = true;

	/// <summary>Mark pre-roll clips as skippable segments so apps can show a Skip button (Jellyfin 12).</summary>
	public bool SkippablePrerolls { get; set; }

	/// <summary>Segment type used for skippable pre-rolls: Intro, Commercial or Preview.</summary>
	public string SkippableType { get; set; } = "Intro";

	/// <summary>A Jellyfin playlist to use as the pre-roll line-up instead of picking clips.</summary>
	public string PlaylistId { get; set; } = string.Empty;

	/// <summary>True: play the next "Clips per play" entries each time, rotating through the playlist. False: play the whole playlist.</summary>
	public bool PlaylistRotate { get; set; } = true;
}
