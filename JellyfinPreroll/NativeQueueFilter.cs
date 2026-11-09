using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
#if JF1010
using Jellyfin.Data.Entities;
#else
using Jellyfin.Database.Implementations.Entities;
#endif
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

/// <summary>
/// Pre-rolls for TV apps that don't run the web player, above all the Jellyfin app on
/// Fire TV / Android TV.
///
/// That app only asks the server for pre-rolls ("intros") before movies. For an episode it builds the
/// play queue itself with <c>GET /Shows/{seriesId}/Episodes?startItemId=…</c>. This filter adds the
/// planned pre-roll clips to that answer (pre-roll → episode → pre-roll → next episode…), so the app
/// plays them with its own player: no flash, no remote control needed, and autoplay keeps going.
///
/// Play / Shuffle on a season or show (<c>GET /Shows/{seriesId}/Episodes?sortBy=…</c> from the Fire TV
/// app) gets the same treatment.
///
/// It also makes <c>GET /Items?ids=…</c> answer in the order asked for when the ids are a pre-roll
/// queue from the server fallback, because the TV app plays that answer in the order it gets it.
///
/// Anything unexpected → the answer is left exactly as Jellyfin made it.
/// </summary>
public sealed class NativeQueueFilter : IAsyncResultFilter
{
	private const string ClientClaim = "Jellyfin-Client";

	private const string DeviceClaim = "Jellyfin-Device";

	private const string UserClaim = "Jellyfin-UserId";

	private static readonly Regex AuthField = new Regex("(\\w+)=\"([^\"]*)\"", RegexOptions.Compiled);

	private readonly ILibraryManager _libraryManager;

	private readonly IDtoService _dtoService;

	private readonly IUserManager _userManager;

	private readonly ILogger<NativeQueueFilter> _logger;

	private readonly PrerollSelector _selector;

	public NativeQueueFilter(ILibraryManager libraryManager, IDtoService dtoService, IUserManager userManager, ILogger<NativeQueueFilter> logger)
	{
		_libraryManager = libraryManager;
		_dtoService = dtoService;
		_userManager = userManager;
		_logger = logger;
		_selector = new PrerollSelector(libraryManager, logger);
	}

	public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
	{
		try
		{
			Apply(context);
		}
		catch (Exception ex)
		{
			// Never break the app's request because of a pre-roll problem.
			_logger.LogWarning(ex, "Pre-Roll Videos: could not add pre-rolls to a TV app's queue");
		}

		await next().ConfigureAwait(false);
	}

	private void Apply(ResultExecutingContext context)
	{
		if (context.ActionDescriptor is not ControllerActionDescriptor action
			|| context.Result is not ObjectResult objectResult
			|| objectResult.Value is not QueryResult<BaseItemDto> result
			|| result.Items is null
			|| result.Items.Count == 0)
		{
			return;
		}

		PluginConfiguration? config = Plugin.Instance?.Configuration;
		if (config is null)
		{
			return;
		}

		HttpContext http = context.HttpContext;
		string client = ClientName(http);
		bool androidTv = PrerollQueuePlanner.IsAndroidTvApp(client);
		bool nativeApp = PrerollQueuePlanner.IsNativeApp(client, config);
		if (!androidTv && !nativeApp)
		{
			return;
		}

		IQueryCollection query = http.Request.Query;
		if (string.Equals(action.ControllerName, "TvShows", StringComparison.OrdinalIgnoreCase)
			&& string.Equals(action.ActionName, "GetEpisodes", StringComparison.OrdinalIgnoreCase))
		{
			if (!config.EnableNativeAppQueue || !nativeApp || query.ContainsKey("adjacentTo"))
			{
				// adjacentTo = the app's "previous episode" button, not something it's about to play.
				return;
			}

			if (query.ContainsKey("startItemId"))
			{
				// Play on an episode: the app plays this list from the top, resuming the first one.
				AddToEpisodeQueue(http, client, result, honorResume: true);
			}
			else if (androidTv && query.ContainsKey("sortBy"))
			{
				// Play / Shuffle on a season or a whole show. The Fire TV app only asks for a sorted
				// episode list when it's about to play it, and it starts the first episode from 0:00.
				AddToEpisodeQueue(http, client, result, honorResume: false);
			}
		}
		else if (string.Equals(action.ControllerName, "Items", StringComparison.OrdinalIgnoreCase)
			&& action.ActionName.StartsWith("GetItems", StringComparison.OrdinalIgnoreCase)
			&& query.ContainsKey("ids")
			&& !query.ContainsKey("sortBy"))
		{
			// Kept even with the TV app setting off: a fallback queue that comes back scrambled would
			// play the episode before its pre-roll.
			KeepRequestedOrder(query, client, result);
		}
	}

	/// <summary>The TV app is about to play this episode list: put the planned pre-rolls in it.</summary>
	private void AddToEpisodeQueue(HttpContext http, string client, QueryResult<BaseItemDto> result, bool honorResume)
	{
		List<BaseItemDto> dtos = result.Items.ToList();
		var items = new List<BaseItem>(dtos.Count);
		foreach (BaseItemDto dto in dtos)
		{
			BaseItem? item = _libraryManager.GetItemById(dto.Id);
			if (item is null)
			{
				// Something we can't look up: keep the list as it is rather than guess.
				return;
			}

			items.Add(item);
		}

		Guid userId = UserId(http);
		string device = Describe(http, client);

		// The app seeks the FIRST item of its queue to the resume point, so an episode that will resume
		// must stay first. (Choosing "play from beginning" on it is covered by the server fallback.)
		long resumeTicks = honorResume ? dtos[0].UserData?.PlaybackPositionTicks ?? 0 : 0;

		QueuePlan plan = PrerollQueuePlanner.Plan(_selector, items, userId, "TV app queue", resumeTicks);

		// The fallback mustn't stop the app later to add a pre-roll this queue deliberately left out.
		DateTime now = DateTime.UtcNow;
		// A new queue replaces whatever an older one planned for these episodes.
		NativeQueueMemory.Forget(userId, items.Select(i => i.Id));
		NativeQueueMemory.Remember(userId, plan.PlannedWithoutClip, now);

		if (plan.Before.Count == 0)
		{
			PrerollActivityLog.Add("TV app queue", device, plan.FirstItemName, plan.Describe(dtos.Count));
			PrerollStats.NativeSeen(device);
			return;
		}

		User? user = userId == Guid.Empty ? null : _userManager.GetUserById(userId);
		bool asTrailer = PrerollQueuePlanner.IsAndroidTvApp(client);
		var clipDtos = new Dictionary<Guid, BaseItemDto>();
		var queue = new List<BaseItemDto>(dtos.Count + plan.Before.Count);
		int added = 0;

		foreach (BaseItemDto dto in dtos)
		{
			if (plan.Before.TryGetValue(dto.Id, out List<BaseItem>? clips))
			{
				foreach (BaseItem clip in clips)
				{
					if (!clipDtos.TryGetValue(clip.Id, out BaseItemDto? clipDto))
					{
						clipDto = ClipDto(clip, user, asTrailer);
						clipDtos[clip.Id] = clipDto;
					}

					queue.Add(clipDto);
					added++;
				}
			}

			queue.Add(dto);
		}

		result.Items = queue;
		result.TotalRecordCount += added;

		PrerollActivityLog.Add("TV app queue", device, plan.FirstItemName, plan.Describe(dtos.Count), plan.FirstSkippedForResume || plan.FirstClips.Length > 0 ? "ok" : "info");
		PrerollStats.NativeSeen(device);
		_logger.LogInformation("Pre-Roll Videos: added {Count} pre-roll(s) to the episode queue of {Device}", added, device);
	}

	/// <summary>
	/// The server fallback sends "play [pre-roll, episode, pre-roll, next episode…]". The TV app looks those
	/// ids up with /Items?ids=… and plays the answer in the order it comes back, which Jellyfin doesn't
	/// keep. Put the answer back in the requested order (repeats included).
	/// </summary>
	private void KeepRequestedOrder(IQueryCollection query, string client, QueryResult<BaseItemDto> result)
	{
		List<Guid> ids = query["ids"]
			.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			.Select(s => Guid.TryParse(s, out Guid g) ? g : Guid.Empty)
			.Where(g => g != Guid.Empty)
			.ToList();
		if (ids.Count < 2)
		{
			return;
		}

		HashSet<Guid> clipIds = _selector.GetPrerollClipIds();
		bool hasClip = ids.Any(clipIds.Contains);
		bool hasOther = ids.Any(id => !clipIds.Contains(id));
		if (!hasClip || !hasOther)
		{
			// Not a pre-roll queue: leave Jellyfin's answer alone.
			return;
		}

		var byId = new Dictionary<Guid, BaseItemDto>();
		foreach (BaseItemDto dto in result.Items)
		{
			byId.TryAdd(dto.Id, dto);
		}

		bool asTrailer = PrerollQueuePlanner.IsAndroidTvApp(client);
		var ordered = new List<BaseItemDto>(ids.Count);
		foreach (Guid id in ids)
		{
			if (byId.TryGetValue(id, out BaseItemDto? dto))
			{
				if (asTrailer && clipIds.Contains(id))
				{
					dto.Type = BaseItemKind.Trailer;
				}

				ordered.Add(dto);
			}
		}

		if (ordered.Count == 0)
		{
			return;
		}

		result.Items = ordered;
		result.TotalRecordCount = ordered.Count;
	}

	private BaseItemDto ClipDto(BaseItem clip, User? user, bool asTrailer)
	{
		BaseItemDto dto = _dtoService.GetBaseItemDto(clip, new DtoOptions(true), user);
		if (asTrailer)
		{
			// Same as the app does with server intros: after a "Trailer" it goes straight to the next item
			// instead of showing its Next Up screen, so pre-roll → episode is seamless.
			dto.Type = BaseItemKind.Trailer;
		}

		return dto;
	}

	internal static string ClientName(HttpContext http)
	{
		string? claim = http.User?.FindFirst(ClientClaim)?.Value;
		return !string.IsNullOrEmpty(claim) ? claim : AuthValue(http, "Client");
	}

	private static Guid UserId(HttpContext http)
		=> Guid.TryParse(http.User?.FindFirst(UserClaim)?.Value, out Guid g) ? g : Guid.Empty;

	private static string Describe(HttpContext http, string client)
	{
		string? device = http.User?.FindFirst(DeviceClaim)?.Value;
		if (string.IsNullOrEmpty(device))
		{
			device = AuthValue(http, "Device");
		}

		return string.IsNullOrEmpty(device) ? client : device + " (" + client + ")";
	}

	private static string AuthValue(HttpContext http, string key)
	{
		string header = http.Request.Headers["Authorization"].ToString();
		if (string.IsNullOrEmpty(header))
		{
			header = http.Request.Headers["X-Emby-Authorization"].ToString();
		}

		foreach (Match m in AuthField.Matches(header))
		{
			if (m.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
			{
				return Uri.UnescapeDataString(m.Groups[2].Value);
			}
		}

		return string.Empty;
	}
}
