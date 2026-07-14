using System.Diagnostics;

namespace PetBox.Web.Logging;

// App-wide request logging: every API/UI/MCP request lands in the self-log (category
// PetBox.* is what SystemLogger captures) with method, path, status, elapsed and the
// authenticated project — 2xx/3xx at Information, 4xx at Warning, 5xx at Error, and any
// unhandled exception at Error. This is the single place activity/errors become
// visible via log_query, instead of scattering logging through controllers/tools.
// Health/version probes are skipped as noise.
//
// 2xx/3xx used to be downgraded to Debug (task self-log-request-noise, commit df867885)
// because this line was 82% of the self-log's volume with zero diagnostic value once a
// request succeeded — but that threw the data away, which spec self-telemetry-log-routing
// forbids. The fix is routing, not discarding: SystemLoggerOptions.Routes sends category
// PetBox.Web.Logging + EventId 500-503 to the `access` log instead of `petbox`, so the
// high-frequency stream no longer crowds out the rest of self-telemetry while still being
// captured in full.
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> log)
{
	public async Task InvokeAsync(HttpContext ctx)
	{
		var path = ctx.Request.Path.Value ?? "";
		if (path is "/health" or "/version")
		{
			await next(ctx);
			return;
		}

		var sw = Stopwatch.StartNew();
		try
		{
			await next(ctx);
			sw.Stop();
			var project = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
			var status = ctx.Response.StatusCode;
			var ms = sw.ElapsedMilliseconds;
			if (status >= 500) LogError(log, ctx.Request.Method, path, status, ms, project);
			else if (status >= 400) LogWarn(log, ctx.Request.Method, path, status, ms, project);
			else LogInfo(log, ctx.Request.Method, path, status, ms, project);
		}
		catch (Exception ex)
		{
			sw.Stop();
			LogException(log, ex, ctx.Request.Method, path, sw.ElapsedMilliseconds);
			throw;
		}
	}

	// Success path (2xx/3xx) is Information again (was downgraded to Debug, see the type-level
	// comment above) — same EventId/template as before so existing queries still match. The
	// volume this generates is why it is routed to `access` rather than filtered out.
	[LoggerMessage(EventId = 500, Level = LogLevel.Information, Message = "{Method} {Path} -> {Status} ({Elapsed} ms) project={Project}")]
	static partial void LogInfo(ILogger logger, string method, string path, int status, long elapsed, string? project);

	[LoggerMessage(EventId = 501, Level = LogLevel.Warning, Message = "{Method} {Path} -> {Status} ({Elapsed} ms) project={Project}")]
	static partial void LogWarn(ILogger logger, string method, string path, int status, long elapsed, string? project);

	[LoggerMessage(EventId = 502, Level = LogLevel.Error, Message = "{Method} {Path} -> {Status} ({Elapsed} ms) project={Project}")]
	static partial void LogError(ILogger logger, string method, string path, int status, long elapsed, string? project);

	[LoggerMessage(EventId = 503, Level = LogLevel.Error, Message = "{Method} {Path} threw after {Elapsed} ms")]
	static partial void LogException(ILogger logger, Exception ex, string method, string path, long elapsed);
}
