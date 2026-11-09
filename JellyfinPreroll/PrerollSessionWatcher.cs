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

	private readonly IServiceProvider _services;

	public PrerollSessionWatcher(ISessionManager sessionManager, ILibraryManager libraryManager, ILogger<PrerollSessionWatcher> logger, IServiceProvider services)
	{
		_services = services;
		_sessionManager = sessionManager;
		_libraryManager = libraryManager;
		_logger = logger;
		_selector = new PrerollSelector(libraryManager, logger);
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
#if JF12
		try
		{
			if (_services.GetService(typeof(ISimilarItemsManager)) is ISimilarItemsManager similar)
			{
				PrerollSelector.UseSimilarItems(similar);
			}
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Pre-Roll Videos: similar titles aren't available");
		}
		if (Plugin.Instance != null)
		{
			Plugin.Instance.ConfigurationChanged += (_, _) => Task.Run(() => SkippableMarker.RunAsync(_services, _libraryManager, _logger));
		}
		Task.Run(async () =>
		{
			await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
			await SkippableMarker.RunAsync(_services, _libraryManager, _logger).ConfigureAwait(false);
		});
#endif
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
				if (orAdd.LastPrerollStartUtc >= orAdd.PendingSinceUtc)
				{
					PrerollActivityLog.Add("Playback", text, text2, "✓ Pre-roll played first (" + orAdd.LastPrerollName + ", added by the server fallback)", "ok");
				}
				else
				{
					PrerollActivityLog.Add("Playback", text, text2, "✗ The app restarted it without playing the pre-roll the server sent", "warn");
				}
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
			bool tvApp = pluginConfiguration.EnableNativeAppQueue && PrerollQueuePlanner.IsNativeApp(session.Client, pluginConfiguration);
			bool stopFirst = tvApp || PrerollQueuePlanner.IsAndroidTvApp(session.Client);
			if (tvApp && NativeQueueMemory.WasPlannedWithoutClip(session.UserId, item.Id, dateTime))
			{
				// The TV app's own queue left this one out (frequency limit / schedule as planned when the
				// queue was built). Stopping the app mid-queue to add one would be worse than skipping it.
				PrerollActivityLog.Add("Playback", text, text2, "No pre-roll: the TV app's queue left this one out when it was planned (frequency limit or schedule), and the server doesn't interrupt that queue");
				return;
			}
			SelectionResult selectionResult2 = _selector.Evaluate(item, new SelectionContext
			{
				UserId = session.UserId,
				AtUtc = dateTime,
				// For TV apps the queue planner below picks this item's clips; don't use up a turn twice.
				DryRun = tvApp
			}, "autoplay", quiet: true);
			List<BaseItem> list2 = ((selectionResult2.Clips.Count > 0) ? selectionResult2.Clips : selectionResult.Clips);
			string text3 = ((selectionResult2.Clips.Count > 0) ? selectionResult2.Reason : selectionResult.Reason);
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
			List<BaseItem> following = (val2 != null && flag2) ? GetFollowingEpisodeItems(val2) : new List<BaseItem>();
			List<Guid> list3 = new List<Guid>();
			if (tvApp)
			{
				// TV app: pre-roll → episode → pre-roll → next episode…, so the episodes that follow
				// don't each start without a pre-roll and need another restart.
				List<BaseItem> queueItems = new List<BaseItem> { item };
				queueItems.AddRange(following);
				QueuePlan plan = PrerollQueuePlanner.Plan(_selector, queueItems, session.UserId, "server fallback");
				if (plan.Before.TryGetValue(item.Id, out List<BaseItem>? planned) && planned.Count > 0)
				{
					list2 = planned;
					text3 = plan.FirstReason;
				}
				foreach (BaseItem queued in queueItems)
				{
					List<BaseItem>? before = queued.Id == item.Id ? list2 : (plan.Before.TryGetValue(queued.Id, out List<BaseItem>? c) ? c : null);
					if (before != null)
					{
						list3.AddRange(before.Select((BaseItem b) => b.Id));
					}
					list3.Add(queued.Id);
				}
			}
			else
			{
				list3.AddRange(list2.Select((BaseItem c) => c.Id));
				list3.Add(item.Id);
				list3.AddRange(following.Select((BaseItem f) => f.Id));
			}
			orAdd.PendingItemId = item.Id;
			orAdd.PendingSinceUtc = dateTime;
			PlayRequest request = new PlayRequest
			{
				ItemIds = list3.ToArray(),
				PlayCommand = (PlayCommand)0,
				StartIndex = 0,
				// The queue starts with a pre-roll: from the top, not from the episode's old position.
				StartPositionTicks = 0
			};
			_logger.LogInformation("Pre-Roll Videos: inserting {Count} pre-roll(s) before \"{Item}\" on {Device}", list2.Count, item.Name, text);
			string clipNames = string.Join(", ", list2.Select((BaseItem c) => c.Name));
			if (stopFirst)
			{
				// The Fire TV / Android TV app closes its player when a play command arrives while a video
				// is playing (the old player's shutdown also closes the new one), so nothing would play at
				// all. Stop the episode first, wait until the app says it stopped, then send the queue.
				PrerollActivityLog.Add("Server fallback", text, text2, "Started without a pre-roll → stopping it, then playing " + clipNames + " first [" + text3 + "]");
				_ = StopThenPlayAsync(session.Id, request, text2, text);
			}
			else
			{
				PrerollActivityLog.Add("Server fallback", text, text2, "Started without a pre-roll → told the app to play " + clipNames + " first [" + text3 + "]");
				_ = SendAsync(session.Id, request, text2, text);
			}
		}
	}

	/// <summary>How long the TV app gets to report that it stopped.</summary>
	internal static TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(6.0);

	/// <summary>Pause after the stop so the app is back on its details screen before the queue arrives.</summary>
	internal static TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(1.5);

	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250.0);

	private bool IsPlaying(string sessionId, out bool sessionGone)
	{
		SessionInfo? s = _sessionManager.Sessions.FirstOrDefault((SessionInfo x) => x.Id == sessionId);
		sessionGone = s == null;
		return s?.NowPlayingItem != null;
	}

	private async Task StopThenPlayAsync(string sessionId, PlayRequest request, string itemName, string device)
	{
		try
		{
			await _sessionManager.SendPlaystateCommand((string)null, sessionId, new PlaystateRequest
			{
				Command = PlaystateCommand.Stop
			}, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);

			var waited = System.Diagnostics.Stopwatch.StartNew();
			bool playing = true;
			bool gone = false;
			while (waited.Elapsed < StopWait)
			{
				await Task.Delay(PollInterval).ConfigureAwait(continueOnCapturedContext: false);
				playing = IsPlaying(sessionId, out gone);
				if (!playing || gone)
				{
					break;
				}
			}

			if (gone || playing)
			{
				ClearPending(sessionId);
				string why = gone ? "the app disconnected" : "the app didn't stop the episode";
				_logger.LogWarning("Pre-Roll Videos: {Why} on {Device}, so no pre-roll was sent for \"{Item}\"", why, device, itemName);
				PrerollActivityLog.Add("Server fallback", device, itemName, "✗ No pre-roll this time: " + why + ", so the server left it alone", "warn");
				return;
			}

			await Task.Delay(SettleDelay).ConfigureAwait(continueOnCapturedContext: false);

			// Someone pressed Play again in the meantime: never interrupt it.
			if (IsPlaying(sessionId, out gone) || gone)
			{
				ClearPending(sessionId);
				PrerollActivityLog.Add("Server fallback", device, itemName, "Something else started playing before the pre-roll was sent, so the server left it alone", "info");
				return;
			}

			await _sessionManager.SendPlayCommand((string)null, sessionId, request, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			ClearPending(sessionId);
			_logger.LogWarning(ex, "Pre-Roll Videos: could not send pre-roll queue for \"{Item}\"", itemName);
			PrerollActivityLog.Add("Server fallback", device, itemName, "✗ The app rejected the command: " + ex.Message, "warn");
		}
	}

	private void ClearPending(string sessionId)
	{
		if (_state.TryGetValue(sessionId, out SessionState? value))
		{
			value.PendingItemId = Guid.Empty;
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

	private List<BaseItem> GetFollowingEpisodeItems(Episode current)
	{
		try
		{
			if (current.SeriesId == Guid.Empty)
			{
				return new List<BaseItem>();
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
			return num >= 0 ? list.Skip(num + 1).Take(MaxQueuedEpisodes).ToList() : new List<BaseItem>();
		}
		catch (Exception exception)
		{
			_logger.LogDebug(exception, "Pre-Roll Videos: could not list following episodes");
			return new List<BaseItem>();
		}
	}
}
