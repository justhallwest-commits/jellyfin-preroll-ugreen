using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;

namespace JellyfinPreroll;

public static class PrerollStats
{
	private static readonly ConcurrentDictionary<Guid, DateTime> _lastByUser = new ConcurrentDictionary<Guid, DateTime>();

	private static readonly ConcurrentDictionary<string, DateTime> _scriptSeen = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

	private static readonly object _fileLock = new object();

	private static StatsFile? _file;

	private static Timer? _saveTimer;

	public static void RecordPlay(BaseItem clip, IEnumerable<Guid> userIds, DateTime utc)
	{
		foreach (Guid item in userIds.Where((Guid u) => u != Guid.Empty).Distinct())
		{
			_lastByUser[item] = utc;
		}
		lock (_fileLock)
		{
			StatsFile statsFile = Load();
			string key = clip.Id.ToString("N");
			if (!statsFile.Clips.TryGetValue(key, out ClipStat value))
			{
				value = new ClipStat();
				statsFile.Clips[key] = value;
			}
			value.Plays++;
			value.LastPlayed = utc.ToString("o");
			statsFile.TotalPlays++;
			ScheduleSave();
		}
	}

	public static DateTime? LastPrerollFor(Guid userId)
	{
		if (!(userId != Guid.Empty) || !_lastByUser.TryGetValue(userId, out var value))
		{
			return null;
		}
		return value;
	}

	public static ClipStat? GetClip(Guid clipId)
	{
		lock (_fileLock)
		{
			ClipStat value;
			return Load().Clips.TryGetValue(clipId.ToString("N"), out value) ? value : null;
		}
	}

	public static (int Total, string Since) Totals()
	{
		lock (_fileLock)
		{
			StatsFile statsFile = Load();
			return (Total: statsFile.TotalPlays, Since: statsFile.Since);
		}
	}

	public static void ResetCounts()
	{
		lock (_fileLock)
		{
			_file = new StatsFile();
			SaveNow();
		}
	}

	public static void ScriptSeen(string device)
	{
		_scriptSeen[string.IsNullOrWhiteSpace(device) ? "(unknown device)" : device] = DateTime.UtcNow;
	}

	public static List<KeyValuePair<string, DateTime>> ScriptDevices()
	{
		return _scriptSeen.OrderByDescending((KeyValuePair<string, DateTime> kv) => kv.Value).ToList();
	}

	private static string? FilePath()
	{
		try
		{
			Plugin? instance = Plugin.Instance;
			string text = ((instance != null) ? ((BasePlugin)instance).DataFolderPath : null);
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			Directory.CreateDirectory(text);
			return Path.Combine(text, "preroll-stats.json");
		}
		catch
		{
			return null;
		}
	}

	private static StatsFile Load()
	{
		if (_file != null)
		{
			return _file;
		}
		try
		{
			string text = FilePath();
			if (text != null && File.Exists(text))
			{
				_file = JsonSerializer.Deserialize<StatsFile>(File.ReadAllText(text)) ?? new StatsFile();
			}
		}
		catch
		{
		}
		return _file ?? (_file = new StatsFile());
	}

	private static void ScheduleSave()
	{
		if (_saveTimer == null)
		{
			_saveTimer = new Timer((object? _) =>
			{
				lock (_fileLock)
				{
					SaveNow();
				}
			}, null, -1, -1);
		}
		_saveTimer.Change(TimeSpan.FromSeconds(30.0), Timeout.InfiniteTimeSpan);
	}

	private static void SaveNow()
	{
		try
		{
			string text = FilePath();
			if (text != null && _file != null)
			{
				string text2 = text + ".tmp";
				File.WriteAllText(text2, JsonSerializer.Serialize(_file));
				File.Move(text2, text, overwrite: true);
			}
		}
		catch
		{
		}
	}
}
