using System;

namespace JellyfinPreroll;

public class ClipInfo
{
	public string Id { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public string[] Tags { get; set; } = Array.Empty<string>();

	public double Seconds { get; set; }

	public bool Enabled { get; set; }

	public bool TooLong { get; set; }

	public bool AvailableNow { get; set; }

	public string Schedule { get; set; } = string.Empty;

	public int Plays { get; set; }

	public string LastPlayed { get; set; } = string.Empty;
}
