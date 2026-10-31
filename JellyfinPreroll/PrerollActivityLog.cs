using System;
using System.Collections.Generic;
using System.Linq;

namespace JellyfinPreroll;

public static class PrerollActivityLog
{
	private const int Capacity = 150;

	private static readonly object _lock = new object();

	private static readonly LinkedList<PrerollActivityEntry> _entries = new LinkedList<PrerollActivityEntry>();

	public static void Add(string source, string device, string item, string message, string level = "info")
	{
		PrerollActivityEntry value = new PrerollActivityEntry
		{
			Time = DateTime.UtcNow.ToString("o"),
			Source = (source ?? string.Empty),
			Device = (device ?? string.Empty),
			Item = (item ?? string.Empty),
			Message = (message ?? string.Empty),
			Level = level
		};
		lock (_lock)
		{
			_entries.AddFirst(value);
			while (_entries.Count > 150)
			{
				_entries.RemoveLast();
			}
		}
	}

	public static List<PrerollActivityEntry> Snapshot()
	{
		lock (_lock)
		{
			return _entries.ToList();
		}
	}

	public static void Clear()
	{
		lock (_lock)
		{
			_entries.Clear();
		}
	}
}
