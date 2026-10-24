using System;
using System.Collections.Generic;
using System.Linq;

namespace JellyfinPreroll;

/// <summary>One line in the "Recent activity" list on the settings page.</summary>
public class PrerollActivityEntry
{
    /// <summary>When it happened (UTC, ISO 8601).</summary>
    public string Time { get; set; } = string.Empty;

    /// <summary>Which part of the plugin: Web player, Play button, Autoplay fallback, Web script.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Device / app, when known.</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Movie or episode involved.</summary>
    public string Item { get; set; } = string.Empty;

    /// <summary>What happened and why.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>ok | info | warn.</summary>
    public string Level { get; set; } = "info";
}

/// <summary>
/// In-memory ring buffer of the plugin's recent decisions, so you can see on the
/// settings page exactly what happened on each play — no log digging needed.
/// </summary>
public static class PrerollActivityLog
{
    private const int Capacity = 150;
    private static readonly object _lock = new();
    private static readonly LinkedList<PrerollActivityEntry> _entries = new();

    public static void Add(string source, string device, string item, string message, string level = "info")
    {
        var entry = new PrerollActivityEntry
        {
            Time = DateTime.UtcNow.ToString("o"),
            Source = source ?? string.Empty,
            Device = device ?? string.Empty,
            Item = item ?? string.Empty,
            Message = message ?? string.Empty,
            Level = level
        };

        lock (_lock)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
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
