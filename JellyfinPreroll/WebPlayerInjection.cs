using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JellyfinPreroll;

public class WebPlayerInjection : IStartupFilter
{
	private const string Marker = "PrerollVideos/ClientScript";

	private readonly ILogger<WebPlayerInjection> _logger;

	private int _loggedOnce;

	public WebPlayerInjection(ILogger<WebPlayerInjection> logger)
	{
		_logger = logger;
	}

	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
	{
		return (IApplicationBuilder app) =>
		{
			app.Use(InvokeAsync);
			next(app);
		};
	}

	private async Task InvokeAsync(HttpContext context, Func<Task> next)
	{
		string value = context.Request.Path.Value;
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration;
		if (!HttpMethods.IsGet(context.Request.Method) || !IsIndexRequest(value) || pluginConfiguration == null || !pluginConfiguration.EnableWebPlayerIntegration)
		{
			await next().ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		context.Request.Headers.Remove("Accept-Encoding");
		context.Request.Headers.Remove("Range");
		context.Request.Headers.Remove("If-Range");
		context.Request.Headers.Remove("If-None-Match");
		context.Request.Headers.Remove("If-Modified-Since");
		Stream originalBody = context.Response.Body;
		using MemoryStream buffer = new MemoryStream();
		context.Response.Body = buffer;
		try
		{
			await next().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch
		{
			context.Response.Body = originalBody;
			throw;
		}
		context.Response.Body = originalBody;
		buffer.Seek(0L, SeekOrigin.Begin);
		if (context.Response.StatusCode != 200 || !(context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false))
		{
			await buffer.CopyToAsync(originalBody).ConfigureAwait(continueOnCapturedContext: false);
			return;
		}
		string text;
		using (StreamReader reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 4096, leaveOpen: true))
		{
			text = await reader.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		try
		{
			string html = text;
			Plugin? instance = Plugin.Instance;
			text = Inject(html, ((instance == null) ? null : ((BasePlugin)instance).Version?.ToString()) ?? "1");
			if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
			{
				_logger.LogInformation("Pre-Roll Videos: web player script added to the Jellyfin web app");
			}
		}
		catch (Exception exception)
		{
			_logger.LogWarning(exception, "Pre-Roll Videos: could not add the web player script (serving the page unchanged)");
		}
		byte[] bytes = Encoding.UTF8.GetBytes(text);
		context.Response.ContentType = "text/html; charset=utf-8";
		context.Response.ContentLength = bytes.Length;
		context.Response.Headers.Remove("ETag");
		context.Response.Headers.Remove("Last-Modified");
		context.Response.Headers.Remove("Accept-Ranges");
		context.Response.Headers["Cache-Control"] = "no-cache";
		await originalBody.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(continueOnCapturedContext: false);
	}

	internal static string Inject(string html, string version)
	{
		if (html.Contains("PrerollVideos/ClientScript", StringComparison.OrdinalIgnoreCase))
		{
			return html;
		}
		string text = "<script src=\"../PrerollVideos/ClientScript?v=" + Uri.EscapeDataString(version) + "\"></script>";
		int num = html.IndexOf("<script", StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			num = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
		}
		if (num < 0)
		{
			num = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
		}
		if (num >= 0)
		{
			return html.Substring(0, num) + text + html.Substring(num);
		}
		return html + text;
	}

	internal static bool IsIndexRequest(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		if (!path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase) && !path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase))
		{
			return path.EndsWith("/web", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
