using System;
using System.Collections.Generic;

namespace JellyfinPreroll;

public class PrerollPlanResponse
{
	public bool Enabled { get; set; }

	public Dictionary<string, string[]> Plan { get; set; } = new Dictionary<string, string[]>();

	public string[] PrerollIds { get; set; } = Array.Empty<string>();
}
