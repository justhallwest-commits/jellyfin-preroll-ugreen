using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
#if JF1010
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

public sealed class PrerollSessionWatcher : IHostedService, IDisposable
{
	private sealed class SessionState
	{
		public DateTime LastPrerollStartUtc { get; set; } = DateTime.MinValue;

		public DateTime LastPrerollStopUtc { get; set; } = DateTime.MinValue;

		public TimeSpan LastPrerollLength { get; set; } = TimeSpan.Zero;

		public string LastPrerollName { get; set; } = string.Empty;

		public Guid PendingItemId { get; set; } = Guid.Empty;

		public DateTime PendingSinceUtc { get; set; } = DateTime.MinValue;
	}

	private static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(45.0);

	private static readonly TimeSpan AfterPrerollGrace = TimeSpan.FromSeconds(30.0);

	private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(10.0);

	private const int MaxQueuedEpisodes = 50;

	private readonly ISessionManager _sessionManager;

	private readonly ILibraryManager _libraryManager;

	private readonly ILogger<PrerollSessionWatcher> _logger;

	private readonly PrerollSelector _selector;

	private readonly ConcurrentDictionary<string, SessionState> _state = new ConcurrentDictionary<string, SessionState>();

	internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

	public PrerollSessionWatcher(ISessionManager sessionManager, ILibraryManager libraryManager, ILogger<PrerollSessionWatcher> logger)
	{
		_sessionManager = sessionManager;
		_libraryManager = libraryManager;
		_logger = logger;
		_selector = new PrerollSelector(libraryManager, logger);
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_sessionManager.PlaybackStart += OnPlaybackStart;
		_sessionManager.PlaybackStopped += OnPlaybackStopped;
		_logger.LogInformation("Pre-Roll Videos: autoplay watcher started");
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Dispose();
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_sessionManager.PlaybackStart -= OnPlaybackStart;
		_sessionManager.PlaybackStopped -= OnPlaybackStopped;
	}

	private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
	{
		try
		{
			SessionInfo session = ((PlaybackProgressEventArgs)e).Session;
			string text = ((session != null) ? session.Id : null);
			if (text != null && _selector.IsPrerollClip(((PlaybackProgressEventArgs)e).Item))
			{
				_state.GetOrAdd(text, (string _) => new SessionState()).LastPrerollStopUtc = UtcNow();
			}
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Pre-Roll Videos: stop handler error");
		}
	}

	private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
	{
		try
		{
			HandleStart(e);
		}
		catch (Exception exception)
		{
			_logger.LogError(exception, "Pre-Roll Videos: autoplay watcher error");
		}
	}

	private void HandleStart(PlaybackProgressEventArgs e)
	{
		SessionInfo session = e.Session;
		BaseItem item = e.Item;
		if (((session != null) ? session.Id : null) == null || item == null)
		{
			return;
		}
		DateTime dateTime = UtcNow();
		SessionState orAdd = _state.GetOrAdd(session.Id, (string _) => new SessionState());
		string text = DescribeDevice(session);
		if (_selector.IsPrerollClip(item))
		{
			orAdd.LastPrerollStartUtc = dateTime;
			orAdd.LastPrerollLength = TimeSpan.FromTicks(item.RunTimeTicks.GetValueOrDefault());
			orAdd.LastPrerollName = item.Name ?? string.Empty;
			List<Guid> list = new List<Guid> { session.UserId };
			if (e.Users != null)
			{
				list.AddRange(e.Users.Select((User u) => u.Id));
			}
			PrerollStats.RecordPlay(item, list, dateTime);
		}
		else
		{
			if (!(item is Episode) && !(item is Movie))
			{
				return;
			}
			PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
			if (pluginConfiguration == null)
			{
				return;
			}
			string text2 = DescribeItem(item);
			if (orAdd.PendingItemId == item.Id && dateTime - orAdd.PendingSinceUtc < PendingTimeout)
			{
				orAdd.PendingItemId = Guid.Empty;
				PrerollActivityLog.Add("Playback", text, text2, "✓ Pre-roll played first (" + orAdd.LastPrerollName + ", added by the server fallback)", "ok");
				return;
			}
			if (dateTime - orAdd.LastPrerollStopUtc < AfterPrerollGrace || dateTime - orAdd.LastPrerollStartUtc < orAdd.LastPrerollLength + AfterPrerollGrace)
			{
				PrerollActivityLog.Add("Playback", text, text2, "✓ Pre-roll played first (" + orAdd.LastPrerollName + ")", "ok");
				return;
			}
			long valueOrDefault = e.PlaybackPositionTicks.GetValueOrDefault();
			if (valueOrDefault > StartWindow.Ticks)
			{
				PrerollActivityLog.Add("Playback", text, text2, "Resumed at " + TimeSpan.FromTicks(valueOrDefault).ToString("h\\:mm\\:ss") + " — no pre-roll on resume (by design)");
				return;
			}
			SelectionResult selectionResult = _selector.Evaluate(item, new SelectionContext
			{
				UserId = session.UserId,
				AtUtc = dateTime,
				DryRun = true
			}, "autoplay check", quiet: true);
			if (selectionResult.Clips.Count == 0)
			{
				PrerollActivityLog.Add("Playback", text, text2, "No pre-roll: " + selectionResult.Reason);
				return;
			}
			if (!pluginConfiguration.PlayBeforeAutoplay)
			{
				PrerollActivityLog.Add("Playback", text, text2, "✗ Started without a pre-roll (server fallback is turned off)", "warn");
				return;
			}
			if (!session.SupportsRemoteControl)
			{
				PrerollActivityLog.Add("Playback", text, text2, "✗ Started without a pre-roll — this app doesn't accept remote play commands, so the server can't add one", "warn");
				return;
			}
			SelectionResult selectionResult2 = _selector.Evaluate(item, new SelectionContext
			{
				UserId = session.UserId,
				AtUtc = dateTime
			}, "autoplay", quiet: true);
			List<BaseItem> list2 = ((selectionResult2.Clips.Count > 0) ? selectionResult2.Clips : selectionResult.Clips);
			string text3 = ((selectionResult2.Clips.Count > 0) ? selectionResult2.Reason : selectionResult.Reason);
			List<Guid> list3 = list2.Select((BaseItem c) => c.Id).ToList();
			list3.Add(item.Id);
			List<User> users = e.Users;
			bool? flag;
			if (users == null)
			{
				flag = null;
			}
			else
			{
				User? val = users.FirstOrDefault();
				flag = ((val != null) ? new bool?(val.EnableNextEpisodeAutoPlay) : ((bool?)null));
			}
			bool flag2 = flag ?? true;
			Episode val2 = (Episode)(object)((item is Episode) ? item : null);
			if ((val2 != null) & flag2)
			{
				list3.AddRange(GetFollowingEpisodes(val2));
			}
			orAdd.PendingItemId = item.Id;
			orAdd.PendingSinceUtc = dateTime;
			PlayRequest request = new PlayRequest
			{
				ItemIds = list3.ToArray(),
				PlayCommand = (PlayCommand)0,
				StartIndex = 0
			};
			_logger.LogInformation("Pre-Roll Videos: inserting {Count} pre-roll(s) before \"{Item}\" on {Device}", list2.Count, item.Name, text);
			PrerollActivityLog.Add("Server fallback", text, text2, "Started without a pre-roll → told the app to play " + string.Join(", ", list2.Select((BaseItem c) => c.Name)) + " first [" + text3 + "]");
			SendAsync(session.Id, request, text2, text);
		}
	}

	private static string DescribeDevice(SessionInfo session)
	{
		string text = session.DeviceName ?? string.Empty;
		string text2 = session.Client ?? string.Empty;
		if (!string.IsNullOrEmpty(text2))
		{
			return text + " (" + text2 + ")";
		}
		return text;
	}

	internal static string DescribeItem(BaseItem item)
	{
		Episode val = (Episode)(object)((item is Episode) ? item : null);
		if (val != null)
		{
			string text = ((((BaseItem)val).ParentIndexNumber.HasValue && ((BaseItem)val).IndexNumber.HasValue) ? $"S{((BaseItem)val).ParentIndexNumber.Value:00}E{((BaseItem)val).IndexNumber.Value:00} " : string.Empty);
			string seriesName = val.SeriesName;
			if (!string.IsNullOrEmpty(seriesName))
			{
				return seriesName + " " + text + "· " + ((BaseItem)val).Name;
			}
			return text + ((BaseItem)val).Name;
		}
		return item.Name ?? string.Empty;
	}

	private async Task SendAsync(string sessionId, PlayRequest request, string itemName, string device)
	{
		try
		{
			await _sessionManager.SendPlayCommand((string)null, sessionId, request, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			if (_state.TryGetValue(sessionId, out SessionState value))
			{
				value.PendingItemId = Guid.Empty;
			}
			_logger.LogWarning(ex, "Pre-Roll Videos: could not send pre-roll queue for \"{Item}\"", itemName);
			PrerollActivityLog.Add("Server fallback", device, itemName, "✗ The app rejected the play command: " + ex.Message, "warn");
		}
	}

	private IEnumerable<Guid> GetFollowingEpisodes(Episode current)
	{
		try
		{
			if (current.SeriesId == Guid.Empty)
			{
				return Array.Empty<Guid>();
			}
			InternalItemsQuery val = new InternalItemsQuery();
			val.AncestorIds = new Guid[1] { current.SeriesId };
			val.IncludeItemTypes = new BaseItemKind[1] { (BaseItemKind)9 };
			val.IsVirtualItem = false;
			val.Recursive = true;
			InternalItemsQuery val2 = val;
			bool currentIsSpecial = (((BaseItem)current).ParentIndexNumber ?? 1) == 0;
			List<BaseItem> list = (from i in _libraryManager.GetItemsResult(val2).Items
				where currentIsSpecial || (i.ParentIndexNumber ?? 1) != 0
				orderby i.ParentIndexNumber ?? int.MaxValue, i.IndexNumber ?? int.MaxValue
				select i).ThenBy((BaseItem i) => i.SortName ?? i.Name, StringComparer.OrdinalIgnoreCase).ToList();
			int num = list.FindIndex((BaseItem i) => i.Id == ((BaseItem)current).Id);
			IEnumerable<Guid> result;
			if (num >= 0)
			{
				IEnumerable<Guid> enumerable = (from i in list.Skip(num + 1).Take(50)
					select i.Id).ToList();
				result = enumerable;
			}
			else
			{
				IEnumerable<Guid> enumerable = Array.Empty<Guid>();
				result = enumerable;
			}
			return result;
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Pre-Roll Videos: could not list following episodes");
			return Array.Empty<Guid>();
		}
	}
}
