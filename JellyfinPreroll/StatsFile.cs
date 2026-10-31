using System;
using System.Collections.Generic;

namespace JellyfinPreroll;

public class StatsFile
{
	public Dictionary<string, ClipStat> Clips { get; set; } = new Dictionary<string, ClipStat>();

	public int TotalPlays { get; set; }

	public string Since { get; set; } = DateTime.UtcNow.ToString("o");
}
