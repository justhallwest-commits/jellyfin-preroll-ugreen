using System;

namespace JellyfinPreroll;

public class PrerollSchedule
{
	public bool Enabled { get; set; } = true;

	public string Name { get; set; } = string.Empty;

	public string ClipTag { get; set; } = string.Empty;

	public string StartDate { get; set; } = string.Empty;

	public string EndDate { get; set; } = string.Empty;

	public int[] Days { get; set; } = Array.Empty<int>();

	public string StartTime { get; set; } = string.Empty;

	public string EndTime { get; set; } = string.Empty;

	public ScheduleMode Mode { get; set; }
}
