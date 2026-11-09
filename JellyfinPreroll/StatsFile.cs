using System;
using System.Collections.Generic;

namespace JellyfinPreroll;

public class StatsFile
{
	public Dictionary<string, ClipStat> Clips { get; set; } = new Dictionary<string, ClipStat>();

	public int TotalPlays { get; set; }

	public string Since { get; set; } = DateTime.UtcNow.ToString("o");

	/// <summary>Shuffle decks: which clips of each pool have had their turn this round.</summary>
	public Dictionary<string, DeckState> Decks { get; set; } = new Dictionary<string, DeckState>();
}

/// <summary>One shuffled deck (one pool of clips, e.g. "general" or "tag:Horror").</summary>
public class DeckState
{
	/// <summary>The clips in this pool the last time it was used.</summary>
	public List<string> Members { get; set; } = new List<string>();

	/// <summary>Clips that have had their turn this round.</summary>
	public List<string> Dealt { get; set; } = new List<string>();

	/// <summary>The clip that played or was picked most recently (never first in the next round).</summary>
	public string Last { get; set; } = string.Empty;

	/// <summary>Completed rounds, for the settings page.</summary>
	public int Rounds { get; set; }

	public string Touched { get; set; } = string.Empty;
}
