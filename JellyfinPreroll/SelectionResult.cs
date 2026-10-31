using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace JellyfinPreroll;

public sealed class SelectionResult
{
	public List<BaseItem> Clips { get; set; } = new List<BaseItem>();

	public string Reason { get; set; } = string.Empty;

	public MediaFacts? Facts { get; set; }

	public List<string> ActiveSchedules { get; set; } = new List<string>();

	public List<string> WantedTags { get; set; } = new List<string>();
}
