using MediaBrowser.Model.Plugins;

namespace JellyfinPreroll;

/// <summary>How pre-rolls are picked from the library.</summary>
public enum PrerollOrder
{
    /// <summary>Random pick, avoiding the clips that played last time when possible.</summary>
    Shuffle = 0,

    /// <summary>Rotate through the library alphabetically, one after another.</summary>
    Sequential = 1
}

/// <summary>
/// Persisted configuration (Dashboard → Plugins → Pre-Roll Videos).
/// Property names from v1.2.6 are unchanged so old settings load as-is.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Library ID holding the pre-roll videos. Empty = plugin disabled.</summary>
    public string PrerollLibraryId { get; set; } = string.Empty;

    /// <summary>How many pre-rolls to play before each item (1–5).</summary>
    public int PrerollCount { get; set; } = 1;

    /// <summary>Play pre-rolls before TV episodes.</summary>
    public bool EnableForTvShows { get; set; } = true;

    /// <summary>Play pre-rolls before movies.</summary>
    public bool EnableForMovies { get; set; } = true;

    /// <summary>Skip pre-roll clips longer than this many seconds. 0 = no limit.</summary>
    public int MaxDurationSeconds { get; set; } = 0;

    /// <summary>Shuffle or sequential rotation.</summary>
    public PrerollOrder Order { get; set; } = PrerollOrder.Shuffle;

    /// <summary>Library IDs whose movies/episodes never get pre-rolls (e.g. Kids, Home Videos).</summary>
    public string[] ExcludedLibraryIds { get; set; } = System.Array.Empty<string>();
}
