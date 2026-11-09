using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace JellyfinPreroll;

/// <summary>Which pre-rolls go in front of which items in a play queue.</summary>
public sealed class QueuePlan
{
	/// <summary>Item id → clips to play right before it.</summary>
	public Dictionary<Guid, List<BaseItem>> Before { get; } = new Dictionary<Guid, List<BaseItem>>();

	public int ItemsWithClips { get; set; }

	public int CooldownSkips { get; set; }

	public string FirstItemName { get; set; } = string.Empty;

	public string FirstClips { get; set; } = string.Empty;

	public string FirstReason { get; set; } = string.Empty;

	public bool FirstSkippedForResume { get; set; }

	/// <summary>Queue items that were checked and got no pre-roll (schedule, frequency limit, no rule…).</summary>
	public List<Guid> PlannedWithoutClip { get; } = new List<Guid>();

	/// <summary>One-line summary for the Activity tab.</summary>
	public string Describe(int queueLength)
	{
		string text;
		if (FirstSkippedForResume)
		{
			text = "Resumes where you left off, so no pre-roll in front of it";
		}
		else if (FirstClips.Length > 0)
		{
			text = "Queued " + FirstClips + " before it [" + FirstReason + "]";
		}
		else
		{
			text = "No pre-roll before it: " + FirstReason;
		}

		if (queueLength > 1)
		{
			bool firstHad = FirstClips.Length > 0 && !FirstSkippedForResume;
			int later = Math.Max(0, ItemsWithClips - (firstHad ? 1 : 0));
			text += "; pre-rolls also queued before " + later + " of the next " + (queueLength - 1) + " item(s)";
			if (CooldownSkips > 0)
			{
				text += " (" + CooldownSkips + " skipped by the frequency limit)";
			}
		}

		return text;
	}
}

/// <summary>
/// Plans pre-rolls along a whole play queue (the item you pressed Play on and the episodes that
/// autoplay after it), working out schedules and the "one every N minutes" limit as time passes.
/// Used for the Fire TV / Android TV app's episode queue and for the server fallback's play command.
/// </summary>
public static class PrerollQueuePlanner
{
	/// <summary>Never plan further ahead than this (the apps ask for up to 150 episodes).</summary>
	public const int MaxItems = 100;

	/// <param name="selector">The clip picker.</param>
	/// <param name="items">The queue, in play order. Only movies and episodes get pre-rolls.</param>
	/// <param name="userId">The viewer.</param>
	/// <param name="trigger">Label for the server log.</param>
	/// <param name="firstResumeTicks">&gt; 0 when the first item resumes part-way: it gets no pre-roll.</param>
	public static QueuePlan Plan(PrerollSelector selector, IReadOnlyList<BaseItem> items, Guid userId, string trigger, long firstResumeTicks = 0)
	{
		var plan = new QueuePlan();
		DateTime t = DateTime.UtcNow;
		DateTime? last = PrerollStats.LastPrerollFor(userId);
		int count = Math.Min(items.Count, MaxItems);

		for (int i = 0; i < count; i++)
		{
			BaseItem item = items[i];
			long runtime = item.RunTimeTicks ?? TimeSpan.FromMinutes(30.0).Ticks;
			if (i == 0)
			{
				plan.FirstItemName = PrerollSessionWatcher.DescribeItem(item);
			}

			if (i == 0 && firstResumeTicks > 0)
			{
				plan.FirstSkippedForResume = true;
				t += TimeSpan.FromTicks(Math.Max(0, runtime - firstResumeTicks));
				continue;
			}

			var ctx = new SelectionContext
			{
				UserId = userId,
				AtUtc = t,
				HasLastPrerollOverride = true,
				LastPrerollUtc = last,
				// Only the first item is about to play; the rest may never be reached.
				PlanAhead = i > 0
			};
			SelectionResult result = selector.Evaluate(item, ctx, trigger, i > 0);
			if (i == 0)
			{
				plan.FirstReason = result.Reason;
				plan.FirstClips = string.Join(", ", result.Clips.Select(c => c.Name));
			}

			if (result.Clips.Count > 0)
			{
				plan.ItemsWithClips++;
				plan.Before[item.Id] = result.Clips.ToList();
				last = t;
				t += TimeSpan.FromTicks(result.Clips.Sum(c => c.RunTimeTicks.GetValueOrDefault()));
			}
			else
			{
				plan.PlannedWithoutClip.Add(item.Id);
				if (result.Reason.StartsWith("a pre-roll played", StringComparison.Ordinal))
				{
					plan.CooldownSkips++;
				}
			}

			t += TimeSpan.FromTicks(runtime);
		}

		return plan;
	}

	/// <summary>True when this app name is one of the TV apps the plugin builds queues for (Settings → General).</summary>
	public static bool IsNativeApp(string? client, PluginConfiguration config)
	{
		if (string.IsNullOrWhiteSpace(client))
		{
			return false;
		}

		var names = (string.IsNullOrWhiteSpace(config.NativeAppClients) ? "Android TV" : config.NativeAppClients)
			.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(n => n.Trim())
			.Where(n => n.Length > 0);
		return names.Any(n => client.Contains(n, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// The Jellyfin app for Fire TV / Android TV (it reports itself as "Jellyfin for Android TV", older
	/// versions as "Android TV"). It moves straight on after "Trailer" items (no Next Up screen), and it
	/// closes its player if it gets a play command while a video is playing.
	/// </summary>
	public static bool IsAndroidTvApp(string? client)
		=> !string.IsNullOrEmpty(client) && client.Contains("Android TV", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Remembers which items a TV app's own queue already planned without a pre-roll (frequency limit,
/// schedule…), so the server fallback doesn't stop the app in the middle of that queue to add one.
/// </summary>
public static class NativeQueueMemory
{
	private static readonly TimeSpan KeepFor = TimeSpan.FromHours(12.0);

	private const int MaxEntries = 5000;

	private static readonly ConcurrentDictionary<(Guid User, Guid Item), DateTime> Planned = new ConcurrentDictionary<(Guid User, Guid Item), DateTime>();

	public static void Remember(Guid userId, IEnumerable<Guid> itemIds, DateTime nowUtc)
	{
		foreach (Guid id in itemIds)
		{
			Planned[(userId, id)] = nowUtc;
		}

		if (Planned.Count > MaxEntries)
		{
			foreach (var entry in Planned.Where(p => nowUtc - p.Value > KeepFor).ToList())
			{
				Planned.TryRemove(entry.Key, out _);
			}

			if (Planned.Count > MaxEntries)
			{
				foreach (var entry in Planned.OrderBy(p => p.Value).Take(Planned.Count - MaxEntries).ToList())
				{
					Planned.TryRemove(entry.Key, out _);
				}
			}
		}
	}

	/// <summary>Forget these items (a new queue planned a pre-roll in front of them).</summary>
	public static void Forget(Guid userId, IEnumerable<Guid> itemIds)
	{
		foreach (Guid id in itemIds)
		{
			Planned.TryRemove((userId, id), out _);
		}
	}

	public static bool WasPlannedWithoutClip(Guid userId, Guid itemId, DateTime nowUtc)
		=> Planned.TryGetValue((userId, itemId), out DateTime at) && nowUtc - at < KeepFor;

	internal static void Clear() => Planned.Clear();
}
