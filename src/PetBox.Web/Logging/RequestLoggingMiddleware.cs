using System.Diagnostics;

namespace PetBox.Web.Logging;

// App-wide request logging: every API/UI/MCP request lands in the self-log (category
// PetBox.* is what SystemLogger captures) with method, path, status, elapsed and the
// authenticated project — and any unhandled exception at Error. This is the single
// place activity/errors become visible via log.query, instead of scattering logging
// through controllers/tools. Health/version probes are skipped as noise.
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

	[LoggerMessage(EventId = 500, Level = LogLevel.Information, Message = "{Method} {Path} -> {Status} ({Elapsed} ms) project={Project}")]
	static partial void LogInfo(ILogger logger, string method, string path, int status, long elapsed, string? project);

	[LoggerMessage(EventId = 501, Level = LogLevel.Warning, Message = "{Method} {Path} -> {Status} ({Elapsed} ms) project={Project}")]
	static partial void LogWarn(ILogger logger, string method, string path, int status, long elapsed, string? project);

	[LoggerMessage(EventId = 502, Level = LogLevel.Error, Message = "{Method} {Path} -> {Status} ({Elapsed} ms) project={Project}")]
	static partial void LogError(ILogger logger, string method, string path, int status, long elapsed, string? project);

	[LoggerMessage(EventId = 503, Level = LogLevel.Error, Message = "{Method} {Path} threw after {Elapsed} ms")]
	static partial void LogException(ILogger logger, Exception ex, string method, string path, long elapsed);
}
