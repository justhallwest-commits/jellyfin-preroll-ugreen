using System.Collections.Generic;

namespace JellyfinPreroll;

public class PreviewResponse
{
	public string ItemName { get; set; } = string.Empty;

	public string ItemType { get; set; } = string.Empty;

	public List<ClipInfo> Clips { get; set; } = new List<ClipInfo>();

	public string Reason { get; set; } = string.Empty;

	public Dictionary<string, string[]> Facts { get; set; } = new Dictionary<string, string[]>();

	public List<string> WantedTags { get; set; } = new List<string>();

	public List<string> ActiveSchedules { get; set; } = new List<string>();

	public string At { get; set; } = string.Empty;
}
