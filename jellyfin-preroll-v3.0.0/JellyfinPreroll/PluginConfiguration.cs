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
}
