using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

[ApiController]
[Route("PrerollVideos")]
public class PrerollController : ControllerBase
{
	private const int MaxPlanItems = 60;

	private const string Admin = "RequiresElevation";

	private static readonly Regex AuthField = new Regex("(\\w+)=\"([^\"]*)\"", RegexOptions.Compiled);

	private static string? _scriptCache;

	private readonly ILibraryManager _libraryManager;

	private readonly PrerollSelector _selector;

	public PrerollController(ILibraryManager libraryManager, ILogger<PrerollController> logger)
	{
		_libraryManager = libraryManager;
		_selector = new PrerollSelector(libraryManager, logger);
	}

	[HttpGet("ClientScript")]
	[AllowAnonymous]
	[Produces("application/javascript", new string[] { })]
	public ActionResult GetClientScript()
	{
		if (_scriptCache == null)
		{
			_scriptCache = LoadScript();
		}
		Response.Headers["Cache-Control"] = "no-cache";
		return Content(_scriptCache, "application/javascript; charset=utf-8");
	}

	[HttpGet("Plan")]
	[Authorize]
	public ActionResult<PrerollPlanResponse> GetPlan([FromQuery] string? itemIds)
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
		PrerollPlanResponse prerollPlanResponse = new PrerollPlanResponse
		{
			Enabled = (pluginConfiguration?.EnableWebPlayerIntegration ?? false)
		};
		if (!prerollPlanResponse.Enabled || pluginConfiguration == null)
		{
			return prerollPlanResponse;
		}
		List<Guid> list = ParseIdList(itemIds);
		prerollPlanResponse.PrerollIds = (from g in _selector.GetPrerollClipIds()
			select g.ToString("N")).ToArray();
		Guid userId = CurrentUserId();
		string device = DescribeCaller();
		DateTime utcNow = DateTime.UtcNow;
		DateTime? lastPrerollUtc = PrerollStats.LastPrerollFor(userId);
		string item = string.Empty;
		string text = string.Empty;
		bool flag = false;
		int num = 0;
		int num2 = 0;
		for (int num3 = 0; num3 < list.Count; num3++)
		{
			BaseItem itemById = _libraryManager.GetItemById(list[num3]);
			if (itemById == null)
			{
				continue;
			}
			SelectionContext ctx = new SelectionContext
			{
				UserId = userId,
				AtUtc = utcNow,
				HasLastPrerollOverride = true,
				LastPrerollUtc = lastPrerollUtc
			};
			SelectionResult selectionResult = _selector.Evaluate(itemById, ctx, "web player", num3 > 0);
			if (num3 == 0)
			{
				item = PrerollSessionWatcher.DescribeItem(itemById);
				flag = selectionResult.Clips.Count > 0;
				text = (flag ? ("Queued " + string.Join(", ", selectionResult.Clips.Select((BaseItem c) => c.Name)) + " before it [" + selectionResult.Reason + "]") : ("No pre-roll before it: " + selectionResult.Reason));
			}
			if (selectionResult.Clips.Count > 0)
			{
				num++;
				lastPrerollUtc = utcNow;
				prerollPlanResponse.Plan[list[num3].ToString("N")] = selectionResult.Clips.Select((BaseItem c) => c.Id.ToString("N")).ToArray();
				utcNow += TimeSpan.FromTicks(selectionResult.Clips.Sum((BaseItem c) => c.RunTimeTicks.GetValueOrDefault()));
			}
			else if (selectionResult.Reason.StartsWith("a pre-roll played", StringComparison.Ordinal))
			{
				num2++;
			}
			utcNow += TimeSpan.FromTicks(itemById.RunTimeTicks ?? TimeSpan.FromMinutes(30.0).Ticks);
		}
		if (list.Count > 0)
		{
			string text2 = text;
			if (list.Count > 1)
			{
				int num4 = Math.Max(0, num - (flag ? 1 : 0));
				text2 = text2 + "; pre-rolls also queued before " + num4 + " of the next " + (list.Count - 1) + " item(s)";
				if (num2 > 0)
				{
					text2 = text2 + " (" + num2 + " skipped by the frequency limit)";
				}
			}
			PrerollActivityLog.Add("Web player", device, item, text2, flag ? "ok" : "info");
		}
		return prerollPlanResponse;
	}

	[HttpPost("ClientStatus")]
	[Authorize]
	public ActionResult PostClientStatus([FromBody] PrerollClientStatus status)
	{
		string device = DescribeCaller();
		bool flag = string.Equals(status?.Status, "hooked", StringComparison.OrdinalIgnoreCase);
		if (flag)
		{
			PrerollStats.ScriptSeen(device);
		}
		string s = (flag ? ("Web player integration active (script " + status?.Version + ")") : ("✗ Web player integration: " + status?.Detail));
		PrerollActivityLog.Add("Web script", device, string.Empty, Trim(s, 300), flag ? "ok" : "warn");
		return NoContent();
	}

	[HttpGet("Health")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult<HealthResponse> GetHealth()
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		DateTime now = DateTime.UtcNow;
		HealthResponse healthResponse = new HealthResponse();
		Plugin? instance = Plugin.Instance;
		healthResponse.Version = ((instance == null) ? null : ((BasePlugin)instance).Version?.ToString()) ?? string.Empty;
		healthResponse.LibraryId = pluginConfiguration.PrerollLibraryId;
		healthResponse.WebIntegration = pluginConfiguration.EnableWebPlayerIntegration;
		healthResponse.ServerFallback = pluginConfiguration.PlayBeforeAutoplay;
		healthResponse.TimeZone = (string.IsNullOrWhiteSpace(pluginConfiguration.TimeZoneId) ? TimeZoneInfo.Local.Id : pluginConfiguration.TimeZoneId);
		healthResponse.ScheduleClock = PrerollSelector.ToScheduleTime(pluginConfiguration, now).ToString("ddd MMM d, h:mm tt");
		HealthResponse healthResponse2 = healthResponse;
		if (Guid.TryParse(pluginConfiguration.PrerollLibraryId, out var result) && result != Guid.Empty)
		{
			BaseItem itemById = _libraryManager.GetItemById(result);
			healthResponse2.LibraryFound = itemById != null;
			healthResponse2.LibraryName = ((itemById != null) ? itemById.Name : null) ?? string.Empty;
		}
		List<BaseItem> allClips = _selector.GetAllClips();
		HashSet<Guid> disabled = PrerollSelector.ParseIds(pluginConfiguration.DisabledClipIds);
		long maxTicks = ((pluginConfiguration.MaxDurationSeconds > 0) ? TimeSpan.FromSeconds(pluginConfiguration.MaxDurationSeconds).Ticks : long.MaxValue);
		healthResponse2.ClipCount = allClips.Count;
		healthResponse2.EnabledCount = Enumerable.Count(allClips, (BaseItem c) => !disabled.Contains(c.Id));
		healthResponse2.TooLongCount = Enumerable.Count(allClips, (BaseItem c) => maxTicks != long.MaxValue && (!c.RunTimeTicks.HasValue || c.RunTimeTicks.Value > maxTicks));
		healthResponse2.UntaggedCount = Enumerable.Count(allClips, (BaseItem c) => PrerollSelector.ClipTags(c).Count == 0);
		healthResponse2.AvailableNowCount = _selector.GetAvailableClips(now).Count;
		healthResponse2.ClipTags = allClips.SelectMany(PrerollSelector.ClipTags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy((string t) => t, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		healthResponse2.ActiveSchedules = _selector.GetActiveSchedules(now).Select(PrerollSelector.Label).ToList();
		healthResponse2.ScriptDevices = (from kv in PrerollStats.ScriptDevices().Take(12)
			select new KeyValuePair<string, string>(kv.Key, kv.Value.ToString("o"))).ToList();
		healthResponse2.RecentWarnings = Enumerable.Count(PrerollActivityLog.Snapshot(), (PrerollActivityEntry a) => a.Level == "warn" && DateTime.TryParse(a.Time, out var result2) && now - result2.ToUniversalTime() < TimeSpan.FromHours(24.0));
		(healthResponse2.TotalPlays, healthResponse2.StatsSince) = PrerollStats.Totals();
		return healthResponse2;
	}

	[HttpGet("Clips")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult<List<ClipInfo>> GetClips()
	{
		DateTime now = DateTime.UtcNow;
		HashSet<Guid> available = (from c in _selector.GetAvailableClips(now)
			select c.Id).ToHashSet();
		return (from c in _selector.GetAllClips()
			select ToClipInfo(c, now, available)).ToList();
	}

	[HttpPost("Clips/{itemId}/Tags")]
	[Authorize(Policy = "RequiresElevation")]
	public async Task<ActionResult<ClipInfo>> SetClipTags([FromRoute] string itemId, [FromBody] ClipTagsBody body, CancellationToken cancellationToken)
	{
		if (!Guid.TryParse(itemId, out var result))
		{
			return BadRequest("bad id");
		}
		BaseItem item = _libraryManager.GetItemById(result);
		if (item == null || !_selector.IsPrerollClip(item))
		{
			return NotFound("not a clip in the pre-roll library");
		}
		item.Tags = (from t in body?.Tags ?? Array.Empty<string>()
			where !string.IsNullOrWhiteSpace(t)
			select t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		await _libraryManager.UpdateItemAsync(item, item.GetParent(), (ItemUpdateType)16, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		PrerollSelector.InvalidateCache();
		DateTime utcNow = DateTime.UtcNow;
		HashSet<Guid> available = (from c in _selector.GetAvailableClips(utcNow)
			select c.Id).ToHashSet();
		return ToClipInfo(item, utcNow, available);
	}

	[HttpGet("Preview")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult<PreviewResponse> GetPreview([FromQuery] string itemId, [FromQuery] string? userId, [FromQuery] string? at)
	{
		if (!Guid.TryParse(itemId, out var result))
		{
			return BadRequest("bad id");
		}
		BaseItem itemById = _libraryManager.GetItemById(result);
		if (itemById == null)
		{
			return NotFound();
		}
		DateTime when = DateTime.UtcNow;
		if (!string.IsNullOrWhiteSpace(at) && DateTime.TryParse(at, null, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result2))
		{
			when = result2;
		}
		SelectionContext ctx = new SelectionContext
		{
			UserId = (Guid.TryParse(userId, out var result3) ? result3 : Guid.Empty),
			AtUtc = when,
			DryRun = true,
			IgnoreCooldown = true
		};
		SelectionResult selectionResult = _selector.Evaluate(itemById, ctx, "test", quiet: true);
		HashSet<Guid> available = (from c in _selector.GetAvailableClips(when)
			select c.Id).ToHashSet();
		PluginConfiguration config = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		PreviewResponse previewResponse = new PreviewResponse
		{
			ItemName = PrerollSessionWatcher.DescribeItem(itemById),
			ItemType = ((object)itemById).GetType().Name,
			Clips = selectionResult.Clips.Select((BaseItem c) => ToClipInfo(c, when, available)).ToList(),
			Reason = selectionResult.Reason,
			WantedTags = selectionResult.WantedTags,
			ActiveSchedules = selectionResult.ActiveSchedules,
			At = PrerollSelector.ToScheduleTime(config, when).ToString("ddd MMM d yyyy, h:mm tt")
		};
		if (selectionResult.Facts != null)
		{
			previewResponse.Facts["Tags"] = selectionResult.Facts.Tags.ToArray();
			previewResponse.Facts["Genres"] = selectionResult.Facts.Genres.ToArray();
			previewResponse.Facts["Studios"] = selectionResult.Facts.Studios.ToArray();
			previewResponse.Facts["Rating"] = selectionResult.Facts.Ratings.ToArray();
			previewResponse.Facts["Library"] = selectionResult.Facts.Libraries.ToArray();
			previewResponse.Facts["Title"] = selectionResult.Facts.Titles.ToArray();
		}
		return previewResponse;
	}

	[HttpGet("Activity")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult<List<PrerollActivityEntry>> GetActivity()
	{
		return PrerollActivityLog.Snapshot();
	}

	[HttpDelete("Activity")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult ClearActivity()
	{
		PrerollActivityLog.Clear();
		return NoContent();
	}

	[HttpPost("Stats/Reset")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult ResetStats()
	{
		PrerollStats.ResetCounts();
		return NoContent();
	}

	private ClipInfo ToClipInfo(BaseItem c, DateTime now, HashSet<Guid> available)
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		HashSet<Guid> hashSet = PrerollSelector.ParseIds(pluginConfiguration.DisabledClipIds);
		ClipStat clip = PrerollStats.GetClip(c.Id);
		double totalSeconds = TimeSpan.FromTicks(c.RunTimeTicks.GetValueOrDefault()).TotalSeconds;
		return new ClipInfo
		{
			Id = c.Id.ToString("N"),
			Name = (c.Name ?? string.Empty),
			Tags = PrerollSelector.ClipTags(c).OrderBy((string t) => t, StringComparer.OrdinalIgnoreCase).ToArray(),
			Seconds = Math.Round(totalSeconds, 1),
			Enabled = !hashSet.Contains(c.Id),
			TooLong = (pluginConfiguration.MaxDurationSeconds > 0 && (!c.RunTimeTicks.HasValue || totalSeconds > (double)pluginConfiguration.MaxDurationSeconds)),
			AvailableNow = available.Contains(c.Id),
			Schedule = _selector.DescribeClipSchedule(c, now),
			Plays = (clip?.Plays ?? 0),
			LastPlayed = (clip?.LastPlayed ?? string.Empty)
		};
	}

	private static List<Guid> ParseIdList(string? raw)
	{
		return (from s in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			select (!Guid.TryParse(s, out var result)) ? Guid.Empty : result into g
			where g != Guid.Empty
			select g).Distinct().Take(60).ToList();
	}

	private Guid CurrentUserId()
	{
		try
		{
			Guid result;
			return Guid.TryParse(User?.Claims?.FirstOrDefault((Claim c) => c.Type == "Jellyfin-UserId")?.Value, out result) ? result : Guid.Empty;
		}
		catch
		{
			return Guid.Empty;
		}
	}

	private string DescribeCaller()
	{
		try
		{
			string text = Request.Headers["Authorization"].ToString();
			if (string.IsNullOrEmpty(text))
			{
				text = Request.Headers["X-Emby-Authorization"].ToString();
			}
			string text2 = string.Empty;
			string text3 = string.Empty;
			foreach (Match item in AuthField.Matches(text))
			{
				string value = item.Groups[1].Value;
				string text4 = Uri.UnescapeDataString(item.Groups[2].Value);
				if (value.Equals("Device", StringComparison.OrdinalIgnoreCase))
				{
					text2 = text4;
				}
				else if (value.Equals("Client", StringComparison.OrdinalIgnoreCase))
				{
					text3 = text4;
				}
			}
			if (text2.Length == 0 && text3.Length == 0)
			{
				return Request.Headers["User-Agent"].ToString();
			}
			return (text3.Length == 0) ? text2 : (text2 + " (" + text3 + ")");
		}
		catch
		{
			return string.Empty;
		}
	}

	private static string Trim(string s, int max)
	{
		if (s.Length > max)
		{
			return s.Substring(0, max) + "…";
		}
		return s;
	}

	private static string LoadScript()
	{
		Assembly executingAssembly = Assembly.GetExecutingAssembly();
		string text = executingAssembly.GetManifestResourceNames().FirstOrDefault((string n) => n.EndsWith("prerollClient.js", StringComparison.OrdinalIgnoreCase));
		if (text == null)
		{
			return "console.warn('[Pre-Roll Videos] client script missing from plugin');";
		}
		using Stream stream = executingAssembly.GetManifestResourceStream(text);
		using StreamReader streamReader = new StreamReader(stream);
		string newValue = executingAssembly.GetName().Version?.ToString() ?? "?";
		return streamReader.ReadToEnd().Replace("%PLUGIN_VERSION%", newValue, StringComparison.Ordinal);
	}
}
