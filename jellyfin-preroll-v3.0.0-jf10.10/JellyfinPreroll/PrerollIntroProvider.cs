using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

public class PrerollIntroProvider : IIntroProvider
{
	private readonly PrerollSelector _selector;

	public string Name => "Pre-Roll Videos";

	public PrerollIntroProvider(ILibraryManager libraryManager, ILogger<PrerollIntroProvider> logger)
	{
		_selector = new PrerollSelector(libraryManager, logger);
	}

	public Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
	{
		SelectionResult selectionResult = _selector.Evaluate(item, new SelectionContext
		{
			UserId = ((user != null) ? user.Id : Guid.Empty)
		}, "play", quiet: false);
		List<BaseItem> clips = selectionResult.Clips;
		string reason = selectionResult.Reason;
		if (item is Episode || item is Movie)
		{
			PrerollActivityLog.Add("App asked for intros", string.Empty, PrerollSessionWatcher.DescribeItem(item), (clips.Count > 0) ? ("Served " + string.Join(", ", clips.Select((BaseItem c) => c.Name)) + " [" + reason + "]") : ("No pre-roll: " + reason), (clips.Count > 0) ? "ok" : "info");
		}
		return Task.FromResult((IEnumerable<IntroInfo>)((IEnumerable<BaseItem>)clips).Select((Func<BaseItem, IntroInfo>)((BaseItem v) =>
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_0017: Expected Obj, but got Unknown
			return new IntroInfo
			{
				ItemId = v.Id
			};
		})).ToList());
	}
}
