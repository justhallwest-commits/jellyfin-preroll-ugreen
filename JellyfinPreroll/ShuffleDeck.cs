using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace JellyfinPreroll;

/// <summary>
/// "Shuffle" order, like a shuffled deck of cards: every clip in a pool gets a turn before any clip
/// comes round again, then the deck is reshuffled (and the clip that just played is never first).
///
/// A turn is counted when a clip is picked for something that's about to play, or when the clip
/// actually starts playing. Picks for items further down a queue (episodes that autoplay later) only
/// reserve the clip, so a queue you stop early doesn't use up the deck.
///
/// Each pool has its own deck (general clips, "tag:Horror", a schedule…), saved with the play counts
/// so restarts don't start a new round.
/// </summary>
public static class ShuffleDeck
{
	private const int MaxDecks = 100;

	private static readonly TimeSpan ReserveFor = TimeSpan.FromHours(6.0);

	private static readonly TimeSpan DealtPlayWindow = TimeSpan.FromHours(12.0);

	private static readonly Random Rng = new Random();

	/// <summary>Clips reserved by a plan-ahead pick (clip id → when).</summary>
	private static readonly ConcurrentDictionary<Guid, DateTime> Reserved = new ConcurrentDictionary<Guid, DateTime>();

	/// <summary>Clips dealt for something about to play, whose play hasn't been seen yet (clip id → when).</summary>
	private static readonly ConcurrentDictionary<Guid, DateTime> AwaitingPlay = new ConcurrentDictionary<Guid, DateTime>();

	public enum Mode
	{
		/// <summary>Test tool / dry runs: change nothing.</summary>
		Preview,

		/// <summary>Further down a queue: reserve only.</summary>
		PlanAhead,

		/// <summary>About to play: the clip has had its turn.</summary>
		Deal
	}

	public static Mode ModeFor(SelectionContext ctx)
		=> ctx.DryRun ? Mode.Preview : ctx.PlanAhead ? Mode.PlanAhead : Mode.Deal;

	public static List<BaseItem> Pick(List<BaseItem> pool, int count, string key, Mode mode)
	{
		count = Math.Min(count, pool.Count);
		if (count <= 0)
		{
			return new List<BaseItem>();
		}

		DateTime now = DateTime.UtcNow;
		return PrerollStats.WithDecks(mode != Mode.Preview, decks =>
		{
			if (!decks.TryGetValue(key, out DeckState? deck))
			{
				deck = new DeckState();
				if (mode != Mode.Preview)
				{
					decks[key] = deck;
					Prune(decks);
				}
			}

			var members = pool.Select(c => Id(c)).ToHashSet(StringComparer.Ordinal);
			var dealt = deck.Dealt.Where(members.Contains).ToHashSet(StringComparer.Ordinal);
			bool roundDone = members.All(dealt.Contains);
			if (roundDone)
			{
				dealt.Clear();
			}

			string last = deck.Last;
			bool avoidLast = dealt.Count == 0 && pool.Count > count;

			// Clips still waiting for their turn: unreserved first (random), then reserved ones (oldest
			// reservation first), the clip that just played last of all, then (only if more clips are
			// needed than are left this round) the next round's.
			var fresh = pool.Where(c => !dealt.Contains(Id(c))).ToList();
			var order = new List<BaseItem>(pool.Count);
			order.AddRange(Shuffled(fresh.Where(c => !IsReserved(c.Id, now) && !(avoidLast && Id(c) == last))));
			order.AddRange(fresh.Where(c => IsReserved(c.Id, now) && !(avoidLast && Id(c) == last))
				.OrderBy(c => Reserved.TryGetValue(c.Id, out DateTime t) ? t : DateTime.MinValue)
				.ThenBy(_ => Rng.Next()));
			order.AddRange(fresh.Where(c => avoidLast && Id(c) == last));
			order.AddRange(Shuffled(pool.Where(c => dealt.Contains(Id(c)) && Id(c) != last)));
			order.AddRange(pool.Where(c => dealt.Contains(Id(c)) && Id(c) == last));

			List<BaseItem> picked = order.Take(count).ToList();

			if (mode == Mode.Deal)
			{
				foreach (BaseItem clip in picked)
				{
					if (members.All(dealt.Contains))
					{
						dealt.Clear();
						deck.Rounds++;
					}

					dealt.Add(Id(clip));
					deck.Last = Id(clip);
					Reserved.TryRemove(clip.Id, out _);
					AwaitingPlay[clip.Id] = now;
				}
			}
			else if (mode == Mode.PlanAhead)
			{
				int i = 0;
				foreach (BaseItem clip in picked)
				{
					// Keep the queue's order, so an unplayed reservation is the first to be reused.
					Reserved[clip.Id] = now.AddTicks(i++);
				}
			}

			if (mode != Mode.Preview)
			{
				if (roundDone)
				{
					deck.Rounds++;
				}

				deck.Members = members.ToList();
				deck.Dealt = dealt.ToList();
				deck.Touched = now.ToString("o");
			}

			return picked;
		});
	}

	/// <summary>A clip started playing (called with the stats lock held).</summary>
	internal static void MarkPlayed(Dictionary<string, DeckState> decks, Guid clipId, DateTime utc)
	{
		string id = clipId.ToString("N");
		Reserved.TryRemove(clipId, out _);
		bool wasDealt = AwaitingPlay.TryRemove(clipId, out DateTime dealtAt) && utc - dealtAt < DealtPlayWindow;

		foreach (DeckState deck in decks.Values)
		{
			if (!deck.Members.Contains(id))
			{
				continue;
			}

			var dealt = deck.Dealt.ToHashSet(StringComparer.Ordinal);
			if (!dealt.Contains(id))
			{
				// Played from a plan-ahead pick (or another pool): that's its turn.
				dealt.Add(id);
			}
			else if (!wasDealt && deck.Members.All(dealt.Contains))
			{
				// Round already complete and this is a new play of it: it opens the next round.
				dealt = new HashSet<string>(StringComparer.Ordinal) { id };
				deck.Rounds++;
			}

			deck.Dealt = dealt.ToList();
			deck.Last = id;
		}
	}

	/// <summary>"3 of 10 played this round" per pool, for the settings page.</summary>
	public static List<(string Pool, int Played, int Total, int Rounds)> Summary()
		=> PrerollStats.WithDecks(false, decks => decks
			.OrderByDescending(d => d.Value.Touched, StringComparer.Ordinal)
			.Select(d =>
			{
				var members = d.Value.Members.ToHashSet(StringComparer.Ordinal);
				return (d.Key, d.Value.Dealt.Count(members.Contains), members.Count, d.Value.Rounds);
			})
			.ToList());

	internal static void ClearMemory()
	{
		Reserved.Clear();
		AwaitingPlay.Clear();
	}

	private static bool IsReserved(Guid id, DateTime now)
		=> Reserved.TryGetValue(id, out DateTime t) && now - t < ReserveFor;

	private static string Id(BaseItem clip) => clip.Id.ToString("N");

	private static IEnumerable<BaseItem> Shuffled(IEnumerable<BaseItem> items)
	{
		List<BaseItem> list = items.ToList();
		lock (Rng)
		{
			for (int i = list.Count - 1; i > 0; i--)
			{
				int j = Rng.Next(i + 1);
				(list[i], list[j]) = (list[j], list[i]);
			}
		}

		return list;
	}

	private static void Prune(Dictionary<string, DeckState> decks)
	{
		if (decks.Count <= MaxDecks)
		{
			return;
		}

		foreach (string key in decks.OrderBy(d => d.Value.Touched, StringComparer.Ordinal).Take(decks.Count - MaxDecks).Select(d => d.Key).ToList())
		{
			decks.Remove(key);
		}
	}
}
