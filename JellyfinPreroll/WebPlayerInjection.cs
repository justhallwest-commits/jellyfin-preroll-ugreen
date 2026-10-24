using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

/// <summary>
/// Adds one &lt;script&gt; tag to the Jellyfin web app's index.html as it is served,
/// so every web-based player (browsers, Jellyfin Media Player, the Android phone app,
/// WebView TV apps) loads the Pre-Roll Videos player script.
///
/// Nothing is written to disk: the page is rewritten in memory per request, so it
/// survives Jellyfin/container updates and needs no file permissions on the NAS.
/// Any problem → the original page is served unchanged.
/// </summary>
public class WebPlayerInjection : IStartupFilter
{
    private const string Marker = "PrerollVideos/ClientScript";
    private readonly ILogger<WebPlayerInjection> _logger;
    private int _loggedOnce;

    public WebPlayerInjection(ILogger<WebPlayerInjection> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Outermost middleware, so the response we see is the plain, uncompressed file.
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        var path = context.Request.Path.Value;
        var config = Plugin.Instance?.Configuration;
        if (!HttpMethods.IsGet(context.Request.Method)
            || !IsIndexRequest(path)
            || config is null
            || !config.EnableWebPlayerIntegration)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Ask downstream for a complete, uncompressed 200.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == 200
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 4096, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        try
        {
            html = Inject(html, Plugin.Instance?.Version?.ToString() ?? "1");
            if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
            {
                _logger.LogInformation("Pre-Roll Videos: web player script added to the Jellyfin web app");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pre-Roll Videos: could not add the web player script (serving the page unchanged)");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        context.Response.Headers["Cache-Control"] = "no-cache";
        await originalBody.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }

    /// <summary>
    /// Insert the script tag before the first script on the page, so it runs before the
    /// web app starts (it needs to be there when the player is created).
    /// The path is relative to /web/, exactly like the web app's own files, so it also
    /// works behind reverse proxies and base URLs.
    /// </summary>
    internal static string Inject(string html, string version)
    {
        if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var tag = "<script src=\"../PrerollVideos/ClientScript?v=" + Uri.EscapeDataString(version) + "\"></script>";

        var at = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            at = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        }

        if (at < 0)
        {
            at = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        }

        return at < 0 ? html + tag : html.Substring(0, at) + tag + html.Substring(at);
    }

    /// <summary>Matches "/web", "/web/", "/web/index.html" (with or without a base URL).</summary>
    internal static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
    }
}
