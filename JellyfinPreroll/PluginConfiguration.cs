using MediaBrowser.Model.Plugins;

namespace JellyfinPreroll;

/// <summary>How pre-rolls are picked from a pool of clips.</summary>
public enum PrerollOrder
{
    /// <summary>Random pick, avoiding the clips that played last time when possible.</summary>
    Shuffle = 0,

    /// <summary>Rotate through the clips alphabetically, one after another.</summary>
    Sequential = 1
}

/// <summary>What to play when tag matching finds no clip for an item.</summary>
public enum NoMatchBehavior
{
    /// <summary>Play clips that have no tags (your general pre-rolls).</summary>
    PlayGeneral = 0,

    /// <summary>Play any clip from the pre-roll library.</summary>
    PlayAny = 1,

    /// <summary>Play no pre-roll.</summary>
    PlayNothing = 2
}

/// <summary>"If the movie/show has tag X, play clips tagged Y."</summary>
public class TagRule
{
    /// <summary>Tag on the movie, show, season or episode.</summary>
    public string MediaTag { get; set; } = string.Empty;

    /// <summary>Tag on the pre-roll clip(s) to play.</summary>
    public string PrerollTag { get; set; } = string.Empty;
}

/// <summary>
/// Persisted configuration (Dashboard → Plugins → Pre-Roll Videos).
/// Property names from earlier versions are unchanged so old settings load as-is.
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

    /// <summary>
    /// Also play pre-rolls before episodes that start by themselves (autoplay / Next)
    /// and on apps that don't request intros.
    /// </summary>
    public bool PlayBeforeAutoplay { get; set; } = true;

    /// <summary>Library IDs whose movies/episodes never get pre-rolls.</summary>
    public string[] ExcludedLibraryIds { get; set; } = System.Array.Empty<string>();

    // ── Tag matching ─────────────────────────────────────────────

    /// <summary>Turn tag matching on/off. Off = every clip can play before everything (v2.0 behaviour).</summary>
    public bool EnableTagMatching { get; set; } = true;

    /// <summary>A clip tagged "Horror" plays before media tagged "Horror" (same tag name, case-insensitive).</summary>
    public bool AutoMatchSameTags { get; set; } = true;

    /// <summary>Extra mappings for when the media tag and clip tag have different names.</summary>
    public TagRule[] TagRules { get; set; } = System.Array.Empty<TagRule>();

    /// <summary>When fewer matching clips exist than "clips per play", top up with general (untagged) clips.</summary>
    public bool FillWithGeneral { get; set; } = true;

    /// <summary>What to play when nothing matches.</summary>
    public NoMatchBehavior WhenNoMatch { get; set; } = NoMatchBehavior.PlayGeneral;

    /// <summary>Media with any of these tags never gets a pre-roll.</summary>
    public string[] SkipTags { get; set; } = System.Array.Empty<string>();
}
