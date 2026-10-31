using System;
using System.Collections.Generic;

namespace JellyfinPreroll;

public class HealthResponse
{
	public string Version { get; set; } = string.Empty;

	public string LibraryId { get; set; } = string.Empty;

	public string LibraryName { get; set; } = string.Empty;

	public bool LibraryFound { get; set; }

	public int ClipCount { get; set; }

	public int EnabledCount { get; set; }

	public int AvailableNowCount { get; set; }

	public int TooLongCount { get; set; }

	public int UntaggedCount { get; set; }

	public string[] ClipTags { get; set; } = Array.Empty<string>();

	public List<string> ActiveSchedules { get; set; } = new List<string>();

	public string ScheduleClock { get; set; } = string.Empty;

	public string TimeZone { get; set; } = string.Empty;

	public bool WebIntegration { get; set; }

	public bool ServerFallback { get; set; }

	public List<KeyValuePair<string, string>> ScriptDevices { get; set; } = new List<KeyValuePair<string, string>>();

	public int RecentWarnings { get; set; }

	public int TotalPlays { get; set; }

	public string StatsSince { get; set; } = string.Empty;
}
