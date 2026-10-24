using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

/// <summary>Response of <c>GET /PrerollVideos/Plan</c>.</summary>
public class PrerollPlanResponse
{
    /// <summary>False = web player integration is off; the script does nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>Item id (N format) → pre-roll clip ids to play right before it.</summary>
    public Dictionary<string, string[]> Plan { get; set; } = new();

    /// <summary>Every clip in the pre-roll library (so the player never doubles up).</summary>
    public string[] PrerollIds { get; set; } = Array.Empty<string>();
}

/// <summary>Body of <c>POST /PrerollVideos/ClientStatus</c>.</summary>
public class PrerollClientStatus
{
    public string Status { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Small API used by the web player script and the settings page.
/// </summary>
[ApiController]
[Route("PrerollVideos")]
public class PrerollController : ControllerBase
{
    private const int MaxPlanItems = 60;
    private static readonly Regex AuthField = new("(\\w+)=\"([^\"]*)\"", RegexOptions.Compiled);
    private static string? _scriptCache;

    private readonly ILibraryManager _libraryManager;
    private readonly PrerollSelector _selector;

    public PrerollController(ILibraryManager libraryManager, ILogger<PrerollController> logger)
    {
        _libraryManager = libraryManager;
        _selector = new PrerollSelector(libraryManager, logger);
    }

    /// <summary>The web player script (injected into the Jellyfin web app automatically).</summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        _scriptCache ??= LoadScript();
        Response.Headers["Cache-Control"] = "no-cache";
        return Content(_scriptCache, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// Which pre-rolls go before each of these items. The web player asks once when you
    /// press Play, for the item and the episodes that will autoplay after it.
    /// </summary>
    [HttpGet("Plan")]
    [Authorize]
    public ActionResult<PrerollPlanResponse> GetPlan([FromQuery] string? itemIds)
    {
        var config = Plugin.Instance?.Configuration;
        var response = new PrerollPlanResponse { Enabled = config is not null && config.EnableWebPlayerIntegration };
        if (!response.Enabled)
        {
            return response;
        }

        var ids = (itemIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .Take(MaxPlanItems)
            .ToList();

        response.PrerollIds = _selector.GetPrerollClipIds().Select(g => g.ToString("N")).ToArray();

        var device = DescribeCaller();
        string firstName = string.Empty, firstReason = string.Empty, firstClips = string.Empty;
        int withClips = 0;

        for (int i = 0; i < ids.Count; i++)
        {
            var item = _libraryManager.GetItemById(ids[i]);
            if (item is null)
            {
                continue;
            }

            var clips = _selector.Select(item, "web player", i > 0, out var reason);
            if (i == 0)
            {
                firstName = PrerollSessionWatcher.DescribeItem(item);
                firstReason = reason;
                firstClips = string.Join(", ", clips.Select(c => c.Name));
            }

            if (clips.Count > 0)
            {
                withClips++;
                response.Plan[ids[i].ToString("N")] = clips.Select(c => c.Id.ToString("N")).ToArray();
            }
        }

        if (ids.Count > 0)
        {
            var msg = firstClips.Length > 0
                ? "Queued " + firstClips + " before it [" + firstReason + "]"
                : "No pre-roll before it: " + firstReason;
            if (ids.Count > 1)
            {
                msg += "; pre-rolls also queued before " + Math.Max(0, withClips - (firstClips.Length > 0 ? 1 : 0)) + " of the next " + (ids.Count - 1) + " item(s)";
            }

            PrerollActivityLog.Add("Web player", device, firstName, msg, firstClips.Length > 0 ? "ok" : "info");
        }

        return response;
    }

    /// <summary>The web script reports whether it could attach to the player.</summary>
    [HttpPost("ClientStatus")]
    [Authorize]
    public ActionResult PostClientStatus([FromBody] PrerollClientStatus status)
    {
        var device = DescribeCaller();
        var ok = string.Equals(status?.Status, "hooked", StringComparison.OrdinalIgnoreCase);
        var text = ok
            ? "Web player integration active (script " + status?.Version + ")"
            : "✗ Web player integration could not attach: " + status?.Detail;
        PrerollActivityLog.Add("Web script", device, string.Empty, Trim(text, 300), ok ? "ok" : "warn");
        return NoContent();
    }

    /// <summary>Recent decisions for the settings page.</summary>
    [HttpGet("Activity")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<List<PrerollActivityEntry>> GetActivity() => PrerollActivityLog.Snapshot();

    /// <summary>Clear the activity list.</summary>
    [HttpDelete("Activity")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult ClearActivity()
    {
        PrerollActivityLog.Clear();
        return NoContent();
    }

    private string DescribeCaller()
    {
        try
        {
            string header = Request.Headers["Authorization"].ToString();
            if (string.IsNullOrEmpty(header))
            {
                header = Request.Headers["X-Emby-Authorization"].ToString();
            }

            string device = string.Empty, client = string.Empty;
            foreach (Match m in AuthField.Matches(header))
            {
                var key = m.Groups[1].Value;
                var val = Uri.UnescapeDataString(m.Groups[2].Value);
                if (key.Equals("Device", StringComparison.OrdinalIgnoreCase))
                {
                    device = val;
                }
                else if (key.Equals("Client", StringComparison.OrdinalIgnoreCase))
                {
                    client = val;
                }
            }

            if (device.Length == 0 && client.Length == 0)
            {
                return Request.Headers["User-Agent"].ToString();
            }

            return client.Length == 0 ? device : device + " (" + client + ")";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static string LoadScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("prerollClient.js", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return "console.warn('[Pre-Roll Videos] client script missing from plugin');";
        }

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var version = asm.GetName().Version?.ToString() ?? "?";
        return reader.ReadToEnd().Replace("%PLUGIN_VERSION%", version, StringComparison.Ordinal);
    }
}
