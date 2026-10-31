using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

public class PrerollSelector
{
	private sealed record PickEnv(PluginConfiguration Config, MediaFacts Facts, List<PrerollSchedule> Active, HashSet<string> ScheduledTags, SelectionContext Ctx);

	private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

	private static readonly object _stateLock = new object();

	private static readonly Random _rng = new Random();

	private static readonly Dictionary<string, HashSet<Guid>> _lastPlayed = new Dictionary<string, HashSet<Guid>>();

	private static readonly Dictionary<string, int> _sequentialIndex = new Dictionary<string, int>();

	private static readonly TimeSpan PoolCacheTime = TimeSpan.FromSeconds(15.0);

	private static readonly object _poolLock = new object();

	private static string _poolKey = string.Empty;

	private static DateTime _poolTimeUtc = DateTime.MinValue;

	private static List<BaseItem> _poolCache = new List<BaseItem>();

	private readonly ILibraryManager _libraryManager;

	private readonly ILogger _logger;

	public PrerollSelector(ILibraryManager libraryManager, ILogger logger)
	{
		_libraryManager = libraryManager;
		_logger = logger;
	}

	public static void InvalidateCache()
	{
		lock (_poolLock)
		{
			_poolKey = string.Empty;
		}
	}

	public bool IsPrerollClip(BaseItem? item)
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
		if (item == null || pluginConfiguration == null || !TryGetLibraryId(pluginConfiguration, out var id))
		{
			return false;
		}
		return GetLibraryIds(item).Contains(id);
	}

	public HashSet<Guid> GetPrerollClipIds()
	{
		return (from v in GetAllClips()
			select v.Id).ToHashSet();
	}

	public List<BaseItem> GetAllClips()
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
		if (pluginConfiguration == null || !TryGetLibraryId(pluginConfiguration, out var id))
		{
			return new List<BaseItem>();
		}
		try
		{
			return GetPrerollVideos(id);
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Pre-Roll Videos: could not list pre-roll clips");
			return new List<BaseItem>();
		}
	}

	public List<BaseItem> GetAvailableClips(DateTime utc)
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
		if (pluginConfiguration == null)
		{
			return new List<BaseItem>();
		}
		HashSet<Guid> disabled = ParseIds(pluginConfiguration.DisabledClipIds);
		long maxTicks = ((pluginConfiguration.MaxDurationSeconds > 0) ? TimeSpan.FromSeconds(pluginConfiguration.MaxDurationSeconds).Ticks : long.MaxValue);
		DateTime local = ToScheduleTime(pluginConfiguration, utc);
		List<PrerollSchedule> source = (pluginConfiguration.Schedules ?? Array.Empty<PrerollSchedule>()).Where((PrerollSchedule s) => s.Enabled && !string.IsNullOrWhiteSpace(s.ClipTag)).ToList();
		HashSet<string> scheduledTags = CleanSet(source.Select((PrerollSchedule s) => s.ClipTag));
		HashSet<string> activeTags = CleanSet(from s in source
			where IsActive(s, local)
			select s.ClipTag);
		return (from v in GetAllClips()
			where !disabled.Contains(v.Id)
			where maxTicks == long.MaxValue || (v.RunTimeTicks.HasValue && v.RunTimeTicks.Value <= maxTicks)
			select v).Where((BaseItem v) =>
		{
			HashSet<string> hashSet = ClipTags(v);
			return !hashSet.Overlaps(scheduledTags) || hashSet.Overlaps(activeTags);
		}).ToList();
	}

	public List<PrerollSchedule> GetActiveSchedules(DateTime utc)
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
		if (pluginConfiguration == null)
		{
			return new List<PrerollSchedule>();
		}
		DateTime local = ToScheduleTime(pluginConfiguration, utc);
		return (pluginConfiguration.Schedules ?? Array.Empty<PrerollSchedule>()).Where((PrerollSchedule s) => IsActive(s, local)).ToList();
	}

	public static DateTime ToScheduleTime(PluginConfiguration config, DateTime utc)
	{
		TimeZoneInfo destinationTimeZone = TimeZoneInfo.Local;
		if (!string.IsNullOrWhiteSpace(config.TimeZoneId))
		{
			try
			{
				destinationTimeZone = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId.Trim());
			}
			catch
			{
				destinationTimeZone = TimeZoneInfo.Local;
			}
		}
		return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), destinationTimeZone);
	}

	public string DescribeClipSchedule(BaseItem clip, DateTime utc)
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
		if (pluginConfiguration == null)
		{
			return string.Empty;
		}
		HashSet<string> tags = ClipTags(clip);
		DateTime local = ToScheduleTime(pluginConfiguration, utc);
		List<PrerollSchedule> list = (pluginConfiguration.Schedules ?? Array.Empty<PrerollSchedule>()).Where((PrerollSchedule s) => s.Enabled && !string.IsNullOrWhiteSpace(s.ClipTag) && tags.Contains(s.ClipTag.Trim())).ToList();
		if (list.Count == 0)
		{
			return string.Empty;
		}
		List<PrerollSchedule> list2 = list.Where((PrerollSchedule s) => IsActive(s, local)).ToList();
		if (list2.Count <= 0)
		{
			return "Seasonal: " + string.Join(", ", list.Select(Label)) + " (not active now)";
		}
		return "Seasonal: " + string.Join(", ", list2.Select(Label)) + " (playing now)";
	}

	public List<BaseItem> Select(BaseItem? item, string trigger)
	{
		string reason;
		return Select(item, trigger, quiet: false, out reason);
	}

	public List<BaseItem> Select(BaseItem? item, string trigger, bool quiet, out string reason)
	{
		SelectionResult selectionResult = Evaluate(item, new SelectionContext(), trigger, quiet);
		reason = selectionResult.Reason;
		return selectionResult.Clips;
	}

	public SelectionResult Evaluate(BaseItem? item, SelectionContext ctx, string trigger, bool quiet)
	{
		try
		{
			return EvaluateInternal(item, ctx, trigger, quiet);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Pre-Roll Videos: failed to pick pre-rolls for {Item}", (item != null) ? item.Name : null);
			return new SelectionResult
			{
				Reason = "error: " + ex.Message
			};
		}
	}

	private SelectionResult EvaluateInternal(BaseItem? item, SelectionContext ctx, string trigger, bool quiet)
	{
		SelectionResult selectionResult = new SelectionResult
		{
			Reason = "plugin not ready"
		};
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
		if (pluginConfiguration == null || item == null)
		{
			return selectionResult;
		}
		if (!TryGetLibraryId(pluginConfiguration, out var id))
		{
			selectionResult.Reason = "no pre-roll library selected";
			return selectionResult;
		}
		if (ctx.UserId != Guid.Empty && ParseIds(pluginConfiguration.SkipUserIds).Contains(ctx.UserId))
		{
			selectionResult.Reason = "pre-rolls are turned off for this user";
			return selectionResult;
		}
		if (!IsEligibleType(item, pluginConfiguration))
		{
			SelectionResult selectionResult2 = selectionResult;
			string reason;
			if (item is Episode)
			{
				reason = "turned off for TV episodes";
			}
			else
			{
				reason = ((item is Movie) ? "turned off for movies" : "not a movie or episode");
			}
			selectionResult2.Reason = reason;
			return selectionResult;
		}
		HashSet<Guid> libraryIds = GetLibraryIds(item);
		if (libraryIds.Contains(id))
		{
			selectionResult.Reason = "item is a pre-roll clip";
			return selectionResult;
		}
		HashSet<Guid> hashSet = ParseIds(pluginConfiguration.ExcludedLibraryIds);
		if (hashSet.Count > 0 && libraryIds.Overlaps(hashSet))
		{
			selectionResult.Reason = "library is in the skip list";
			return selectionResult;
		}
		MediaFacts mediaFacts = (selectionResult.Facts = GetFacts(item));
		HashSet<string> hashSet2 = CleanSet(pluginConfiguration.SkipTags);
		string text = mediaFacts.Tags.Concat(mediaFacts.Genres).FirstOrDefault(hashSet2.Contains);
		if (text != null)
		{
			selectionResult.Reason = "has skip tag/genre \"" + text + "\"";
			return selectionResult;
		}
		if (pluginConfiguration.CooldownMinutes > 0 && !ctx.IgnoreCooldown)
		{
			DateTime? dateTime = (ctx.HasLastPrerollOverride ? ctx.LastPrerollUtc : PrerollStats.LastPrerollFor(ctx.UserId));
			if (dateTime.HasValue)
			{
				TimeSpan timeSpan = ctx.AtUtc - dateTime.Value;
				if (timeSpan >= TimeSpan.Zero && timeSpan < TimeSpan.FromMinutes(pluginConfiguration.CooldownMinutes))
				{
					selectionResult.Reason = "a pre-roll played " + (int)timeSpan.TotalMinutes + " min ago (limit: one every " + pluginConfiguration.CooldownMinutes + " min)";
					return selectionResult;
				}
			}
		}
		HashSet<Guid> disabled = ParseIds(pluginConfiguration.DisabledClipIds);
		long maxTicks = ((pluginConfiguration.MaxDurationSeconds > 0) ? TimeSpan.FromSeconds(pluginConfiguration.MaxDurationSeconds).Ticks : long.MaxValue);
		DateTime local = ToScheduleTime(pluginConfiguration, ctx.AtUtc);
		List<PrerollSchedule> source = (pluginConfiguration.Schedules ?? Array.Empty<PrerollSchedule>()).Where((PrerollSchedule s) => s.Enabled && !string.IsNullOrWhiteSpace(s.ClipTag)).ToList();
		List<PrerollSchedule> list = source.Where((PrerollSchedule s) => IsActive(s, local)).ToList();
		selectionResult.ActiveSchedules = list.Select(Label).ToList();
		HashSet<string> scheduledTags = CleanSet(source.Select((PrerollSchedule s) => s.ClipTag));
		HashSet<string> activeTags = CleanSet(list.Select((PrerollSchedule s) => s.ClipTag));
		List<BaseItem> list2 = (from v in GetPrerollVideos(id)
			where !disabled.Contains(v.Id)
			where maxTicks == long.MaxValue || (v.RunTimeTicks.HasValue && v.RunTimeTicks.Value <= maxTicks)
			select v).Where((BaseItem v) =>
		{
			HashSet<string> hashSet3 = ClipTags(v);
			return !hashSet3.Overlaps(scheduledTags) || hashSet3.Overlaps(activeTags);
		}).ToList();
		if (list2.Count == 0)
		{
			selectionResult.Reason = "no pre-roll clips available right now (check the Clips tab)";
			_logger.LogWarning("Pre-Roll Videos: no usable clips in pre-roll library {Id}", id);
			return selectionResult;
		}
		PickEnv env = new PickEnv(pluginConfiguration, mediaFacts, list, scheduledTags, ctx);
		int count = Math.Clamp(pluginConfiguration.PrerollCount, 1, 5);
		List<SequenceSlot> list3 = (pluginConfiguration.Sequence ?? Array.Empty<SequenceSlot>()).Where((SequenceSlot s) => SlotApplies(s, item)).ToList();
		if (list3.Count > 0)
		{
			List<BaseItem> chosen = new List<BaseItem>();
			List<string> list4 = new List<string>();
			foreach (SequenceSlot item2 in list3)
			{
				List<BaseItem> list5 = list2.Where((BaseItem c) => chosen.All((BaseItem x) => x.Id != c.Id)).ToList();
				if (item2.Kind == SlotKind.Tag)
				{
					string tag = (item2.ClipTag ?? string.Empty).Trim();
					List<BaseItem> list6 = Pick(list5.Where((BaseItem c) => ClipTags(c).Contains(tag)).ToList(), 1, "seq:" + tag, pluginConfiguration.Order, ctx.DryRun);
					chosen.AddRange(list6);
					list4.Add((list6.Count > 0) ? tag : ("(no clip tagged " + tag + ")"));
				}
				else
				{
					List<BaseItem> list7 = PickCore(list5, 1, env, out string reason2, selectionResult.WantedTags);
					chosen.AddRange(list7);
					list4.Add((list7.Count > 0) ? reason2 : ("(nothing: " + reason2 + ")"));
				}
			}
			selectionResult.Clips = chosen;
			selectionResult.Reason = "sequence: " + string.Join(" → ", list4);
		}
		else
		{
			selectionResult.Clips = PickCore(list2, count, env, out string reason3, selectionResult.WantedTags);
			selectionResult.Reason = reason3;
		}
		LogLevel logLevel = ((quiet || ctx.DryRun) ? LogLevel.Debug : LogLevel.Information);
		if (selectionResult.Clips.Count == 0)
		{
			_logger.Log(logLevel, "Pre-Roll Videos ({Trigger}): nothing to play before \"{Item}\" ({Reason})", trigger, item.Name, selectionResult.Reason);
		}
		else
		{
			_logger.Log(logLevel, "Pre-Roll Videos ({Trigger}): {Count} pre-roll(s) before \"{Item}\" [{Reason}]: {Clips}", trigger, selectionResult.Clips.Count, item.Name, selectionResult.Reason, string.Join(", ", selectionResult.Clips.Select((BaseItem v) => v.Name)));
		}
		return selectionResult;
	}

	private List<BaseItem> PickCore(List<BaseItem> pool, int count, PickEnv env, out string reason, List<string> wantedOut)
	{
		PluginConfiguration config = env.Config;
		bool dryRun = env.Ctx.DryRun;
		List<PrerollSchedule> list = env.Active.Where((PrerollSchedule s) => s.Mode == ScheduleMode.Takeover).ToList();
		if (list.Count > 0)
		{
			HashSet<string> tags = CleanSet(list.Select((PrerollSchedule s) => s.ClipTag));
			List<BaseItem> list2 = pool.Where((BaseItem c) => ClipTags(c).Overlaps(tags)).ToList();
			if (list2.Count > 0)
			{
				reason = "schedule " + string.Join(", ", list.Select(Label)) + " (plays before everything)";
				return Pick(list2, count, "sched:" + string.Join("|", tags.OrderBy((string t) => t, Ci)), config.Order, dryRun);
			}
		}
		List<PrerollSchedule> source = env.Active.Where((PrerollSchedule s) => s.Mode == ScheduleMode.ReplaceGeneral).ToList();
		List<PrerollSchedule> source2 = env.Active.Where((PrerollSchedule s) => s.Mode == ScheduleMode.Mix).ToList();
		HashSet<string> replaceTags = CleanSet(source.Select((PrerollSchedule s) => s.ClipTag));
		HashSet<string> mixTags = CleanSet(source2.Select((PrerollSchedule s) => s.ClipTag));
		List<BaseItem> list3 = pool.Where((BaseItem c) => ClipTags(c).Overlaps(replaceTags)).ToList();
		List<BaseItem> list4 = pool.Where((BaseItem c) => ClipTags(c).Overlaps(mixTags)).ToList();
		List<BaseItem> list5;
		string text;
		if (list3.Count > 0)
		{
			list5 = list3;
			text = "schedule " + string.Join(", ", source.Select(Label));
		}
		else
		{
			list5 = (from c in (config.EnableTagMatching ? pool.Where((BaseItem c) => ClipTags(c).Count == 0) : pool.Where((BaseItem c) => !ClipTags(c).Overlaps(env.ScheduledTags))).Concat(list4)
				group c by c.Id into g
				select g.First()).ToList();
			if (list4.Count > 0)
			{
				text = (config.EnableTagMatching ? "general clips" : "all clips") + " + schedule " + string.Join(", ", source2.Select(Label));
			}
			else
			{
				text = (config.EnableTagMatching ? "general clips" : "all clips");
			}
		}
		if (!config.EnableTagMatching)
		{
			reason = text;
			return Pick(list5, count, "general:" + text, config.Order, dryRun);
		}
		Dictionary<string, string> wanted = new Dictionary<string, string>(Ci);
		if (config.AutoMatchSameTags)
		{
			Want(env.Facts.Tags, "tag");
		}
		if (config.AutoMatchGenres)
		{
			Want(env.Facts.Genres, "genre");
		}
		if (config.AutoMatchStudios)
		{
			Want(env.Facts.Studios, "studio");
		}
		if (config.AutoMatchRatings)
		{
			Want(env.Facts.Ratings, "rating");
		}
		MatchRule[] array = config.MatchRules ?? Array.Empty<MatchRule>();
		foreach (MatchRule matchRule in array)
		{
			string text2 = matchRule?.Value?.Trim();
			string text3 = matchRule?.ClipTag?.Trim();
			if (!string.IsNullOrEmpty(text2) && !string.IsNullOrEmpty(text3) && env.Facts.For(matchRule.Field).Contains(text2))
			{
				wanted.TryAdd(text3, "rule: " + matchRule.Field.ToString() + " = " + text2);
			}
		}
		TagRule[] array2 = config.TagRules ?? Array.Empty<TagRule>();
		foreach (TagRule obj in array2)
		{
			string text4 = obj?.MediaTag?.Trim();
			string text5 = obj?.PrerollTag?.Trim();
			if (!string.IsNullOrEmpty(text4) && !string.IsNullOrEmpty(text5) && env.Facts.Tags.Contains(text4))
			{
				wanted.TryAdd(text5, "rule: Tag = " + text4);
			}
		}
		foreach (KeyValuePair<string, string> item2 in wanted)
		{
			string item = item2.Key + " (" + item2.Value + ")";
			if (!wantedOut.Contains(item))
			{
				wantedOut.Add(item);
			}
		}
		List<BaseItem> list6 = ((wanted.Count == 0) ? new List<BaseItem>() : pool.Where((BaseItem c) => ClipTags(c).Any(wanted.ContainsKey)).ToList());
		if (list6.Count > 0)
		{
			List<string> list7 = list6.SelectMany(ClipTags).Where(wanted.ContainsKey).Distinct(Ci)
				.OrderBy((string t) => t, Ci)
				.ToList();
			List<BaseItem> list8 = Pick(list6, count, "tag:" + string.Join("|", list7), config.Order, dryRun);
			reason = "matched " + string.Join(", ", list7.Select((string t) => t + " (" + wanted[t] + ")"));
			if (list8.Count < count && config.FillWithGeneral && list5.Count > 0)
			{
				HashSet<Guid> ids = list8.Select((BaseItem p) => p.Id).ToHashSet();
				List<BaseItem> list9 = Pick(list5.Where((BaseItem g) => !ids.Contains(g.Id)).ToList(), count - list8.Count, "general:" + text, config.Order, dryRun);
				list8.AddRange(list9);
				if (list9.Count > 0)
				{
					reason = reason + " + " + text;
				}
			}
			return list8;
		}
		switch (config.WhenNoMatch)
		{
		case NoMatchBehavior.PlayNothing:
			reason = "no match, set to play nothing";
			return new List<BaseItem>();
		case NoMatchBehavior.PlayAny:
			reason = "no match, any clip";
			return Pick(pool, count, "all", config.Order, dryRun);
		default:
			reason = ((list5.Count > 0) ? ("no match, " + text) : "no match and no untagged (general) clips");
			return Pick(list5, count, "general:" + text, config.Order, dryRun);
		}
		void Want(IEnumerable<string> values, string value)
		{
			foreach (string value in values)
			{
				wanted.TryAdd(value, value);
			}
		}
	}

	private MediaFacts GetFacts(BaseItem item)
	{
		MediaFacts mediaFacts = new MediaFacts();
		try
		{
			mediaFacts.Tags.UnionWith(Clean(item.GetInheritedTags()));
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Pre-Roll Videos: could not read inherited tags for {Item}", item.Name);
			mediaFacts.Tags.UnionWith(Clean(item.Tags));
		}
		mediaFacts.Genres.UnionWith(Clean(item.Genres));
		mediaFacts.Studios.UnionWith(Clean(item.Studios));
		AddIf(mediaFacts.Ratings, item.OfficialRating);
		Episode val = (Episode)(object)((item is Episode) ? item : null);
		if (val != null)
		{
			mediaFacts.Types.Add("Episode");
			AddIf(mediaFacts.Titles, val.SeriesName);
			Series val2 = null;
			try
			{
				val2 = val.Series;
			}
			catch
			{
			}
			if (val2 != null)
			{
				mediaFacts.Genres.UnionWith(Clean(((BaseItem)val2).Genres));
				mediaFacts.Studios.UnionWith(Clean(((BaseItem)val2).Studios));
				AddIf(mediaFacts.Ratings, ((BaseItem)val2).OfficialRating);
				AddIf(mediaFacts.Titles, ((BaseItem)val2).Name);
			}
		}
		else if (item is Movie)
		{
			mediaFacts.Types.Add("Movie");
			AddIf(mediaFacts.Titles, item.Name);
		}
		try
		{
			foreach (Folder collectionFolder in _libraryManager.GetCollectionFolders(item))
			{
				AddIf(mediaFacts.Libraries, ((BaseItem)collectionFolder).Name);
			}
		}
		catch
		{
		}
		return mediaFacts;
	}

	private static void AddIf(HashSet<string> set, string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			set.Add(value.Trim());
		}
	}

	internal static string Label(PrerollSchedule s)
	{
		return "\"" + (string.IsNullOrWhiteSpace(s.Name) ? s.ClipTag : s.Name) + "\"";
	}

	internal static bool IsActive(PrerollSchedule s, DateTime local)
	{
		if (s == null || !s.Enabled || string.IsNullOrWhiteSpace(s.ClipTag))
		{
			return false;
		}
		DateTime dateTime = local.Date;
		if (TryTime(s.StartTime, out var value) && TryTime(s.EndTime, out var value2) && value != value2)
		{
			TimeSpan timeOfDay = local.TimeOfDay;
			if (value < value2)
			{
				if (timeOfDay < value || timeOfDay >= value2)
				{
					return false;
				}
			}
			else if (timeOfDay < value2)
			{
				dateTime = dateTime.AddDays(-1.0);
			}
			else if (timeOfDay < value)
			{
				return false;
			}
		}
		int[] days = s.Days;
		if (days != null && days.Length > 0 && !Enumerable.Contains(s.Days, (int)dateTime.DayOfWeek))
		{
			return false;
		}
		bool flag = TryMonthDay(s.StartDate, out var value3);
		bool flag2 = TryMonthDay(s.EndDate, out var value4);
		int num = dateTime.Month * 100 + dateTime.Day;
		if (flag & flag2)
		{
			if (value3 > value4)
			{
				if (num < value3)
				{
					return num <= value4;
				}
				return true;
			}
			if (num >= value3)
			{
				return num <= value4;
			}
			return false;
		}
		if (flag)
		{
			return num >= value3;
		}
		if (flag2)
		{
			return num <= value4;
		}
		return true;
	}

	private static bool TryMonthDay(string? s, out int value)
	{
		value = 0;
		if (string.IsNullOrWhiteSpace(s))
		{
			return false;
		}
		string[] array = s.Trim().Split('-', '/', '.');
		if (array.Length != 2 || !int.TryParse(array[0], out var result) || !int.TryParse(array[1], out var result2) || result < 1 || result > 12 || result2 < 1 || result2 > 31)
		{
			return false;
		}
		value = result * 100 + result2;
		return true;
	}

	private static bool TryTime(string? s, out TimeSpan value)
	{
		value = TimeSpan.Zero;
		if (!string.IsNullOrWhiteSpace(s))
		{
			return TimeSpan.TryParseExact(s.Trim(), new string[2] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out value);
		}
		return false;
	}

	private static bool SlotApplies(SequenceSlot slot, BaseItem item)
	{
		return slot.AppliesTo switch
		{
			SlotAppliesTo.Movies => item is Movie, 
			SlotAppliesTo.Episodes => item is Episode, 
			_ => true, 
		};
	}

	private static bool IsEligibleType(BaseItem item, PluginConfiguration config)
	{
		if (!(item is Episode))
		{
			if (item is Movie)
			{
				return config.EnableForMovies;
			}
			return false;
		}
		return config.EnableForTvShows;
	}

	private static bool TryGetLibraryId(PluginConfiguration config, out Guid id)
	{
		if (Guid.TryParse(config.PrerollLibraryId, out id))
		{
			return id != Guid.Empty;
		}
		return false;
	}

	internal static HashSet<string> ClipTags(BaseItem clip)
	{
		return CleanSet(clip.Tags);
	}

	private static IEnumerable<string> Clean(IEnumerable<string>? values)
	{
		return from v in values ?? Array.Empty<string>()
			where !string.IsNullOrWhiteSpace(v)
			select v.Trim();
	}

	private static HashSet<string> CleanSet(IEnumerable<string>? values)
	{
		return new HashSet<string>(Clean(values), Ci);
	}

	private HashSet<Guid> GetLibraryIds(BaseItem item)
	{
		try
		{
			return (from f in _libraryManager.GetCollectionFolders(item)
				select ((BaseItem)f).Id).ToHashSet();
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Pre-Roll Videos: could not resolve libraries for {Item}", item.Name);
			return new HashSet<Guid>();
		}
	}

	internal static HashSet<Guid> ParseIds(IEnumerable<string>? raw)
	{
		HashSet<Guid> hashSet = new HashSet<Guid>();
		foreach (string item in raw ?? Array.Empty<string>())
		{
			if (Guid.TryParse(item, out var result) && result != Guid.Empty)
			{
				hashSet.Add(result);
			}
		}
		return hashSet;
	}

	private List<BaseItem> GetPrerollVideos(Guid libraryId)
	{
		string text = libraryId.ToString("N");
		lock (_poolLock)
		{
			if (_poolKey == text && DateTime.UtcNow - _poolTimeUtc < PoolCacheTime)
			{
				return _poolCache.ToList();
			}
		}
		InternalItemsQuery val = new InternalItemsQuery();
		val.ParentId = libraryId;
		val.IncludeItemTypes = new[] { BaseItemKind.Video, BaseItemKind.Movie, BaseItemKind.MusicVideo };
		val.IsVirtualItem = false;
		val.Recursive = true;
		InternalItemsQuery val2 = val;
		List<BaseItem> list = _libraryManager.GetItemsResult(val2).Items.OrderBy((BaseItem v) => v.SortName ?? v.Name, Ci).ToList();
		lock (_poolLock)
		{
			_poolKey = text;
			_poolTimeUtc = DateTime.UtcNow;
			_poolCache = list;
		}
		return list.ToList();
	}

	private static List<BaseItem> Pick(List<BaseItem> pool, int count, string key, PrerollOrder order, bool dryRun)
	{
		count = Math.Min(count, pool.Count);
		if (count <= 0)
		{
			return new List<BaseItem>();
		}
		lock (_stateLock)
		{
			if (order == PrerollOrder.Sequential)
			{
				_sequentialIndex.TryGetValue(key, out var value);
				List<BaseItem> list = new List<BaseItem>(count);
				for (int i = 0; i < count; i++)
				{
					list.Add(pool[(value + i) % pool.Count]);
				}
				if (!dryRun)
				{
					_sequentialIndex[key] = (value + count) % pool.Count;
				}
				return list;
			}
			_lastPlayed.TryGetValue(key, out HashSet<Guid> last);
			if (last == null)
			{
				last = new HashSet<Guid>();
			}
			List<BaseItem> list2 = pool.Where((BaseItem v) => !last.Contains(v.Id)).ToList();
			List<BaseItem> list3 = ((list2.Count >= count) ? list2 : pool).OrderBy((BaseItem _) => _rng.Next()).Take(count).ToList();
			if (!dryRun)
			{
				_lastPlayed[key] = list3.Select((BaseItem v) => v.Id).ToHashSet();
			}
			return list3;
		}
	}
}
