using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using PetBox.Core.Features;

namespace PetBox.Web.Mcp;

// Shared guards + JSON helpers for the tasks.*/memory.*/session.* MCP tools.
// Mirrors the private AssertProject/AssertScope helpers in DataTools/LogTools,
// factored out so the three new tool classes don't each copy them. Claims
// ("project", "scopes") are set by ApiKeyAuthenticationHandler.
static partial class ModuleMcp
{
	public static void AssertProject(IHttpContextAccessor http, string projectKey)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (string.IsNullOrEmpty(claim) || !string.Equals(claim, projectKey, StringComparison.Ordinal))
			throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{projectKey}'");
	}

	public static void AssertScope(IHttpContextAccessor http, string required)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		var parts = scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (!parts.Contains(required, StringComparer.Ordinal))
			throw new UnauthorizedAccessException($"ApiKey lacks required scope '{required}'");
	}

	public static void AssertFeature(FeatureFlags features, Feature feature)
	{
		if (!features.IsEnabled(feature))
			throw new InvalidOperationException($"feature '{feature}' is disabled");
	}

	// Logger factory captured at startup so the static GuardAsync can log into the
	// `PetBox.Web.Mcp.*` category — which the self-log (SystemLogger, prefix "PetBox",
	// min Information) actually captures. Without this, MCP tool activity/errors never
	// reach the $system self-log (the framework logs under ModelContextProtocol.*,
	// which is filtered out).
	static ILoggerFactory? _loggers;

	public static void Configure(ILoggerFactory loggers) => _loggers = loggers;

	// Runs a tool body: logs the invocation (Information) and any failure (Error) into
	// the self-log, and converts a thrown exception into a structured, agent-readable
	// error result instead of the MCP framework's opaque "An error occurred invoking
	// 'X'". Surfaces the cause (scope/feature/project assert, or a deeper server-side
	// failure with message + stack) both to the agent and to the self-log.
	public static async Task<object> GuardAsync(string tool, Func<Task<object>> body)
	{
		var log = _loggers?.CreateLogger("PetBox.Web.Mcp");
		if (log is not null) LogInvoked(log, tool);
		try
		{
			return await body();
		}
		catch (Exception ex)
		{
			if (log is not null) LogFailed(log, ex, tool);
			return new { error = new { type = ex.GetType().Name, message = ex.Message, detail = ex.ToString() } };
		}
	}

	[LoggerMessage(EventId = 400, Level = LogLevel.Information, Message = "mcp tool {Tool} invoked")]
	static partial void LogInvoked(ILogger logger, string tool);

	[LoggerMessage(EventId = 401, Level = LogLevel.Error, Message = "mcp tool {Tool} failed")]
	static partial void LogFailed(ILogger logger, Exception ex, string tool);

	public static string? OptStr(JsonElement o, string name) =>
		o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
			? e.GetString()
			: null;

	public static string ReqStr(JsonElement o, string name)
	{
		var v = OptStr(o, name);
		if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException($"{name} is required");
		return v!;
	}

	public static long OptLong(JsonElement o, string name, long dflt) =>
		o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var e)
			&& e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)
			? v
			: dflt;
}
