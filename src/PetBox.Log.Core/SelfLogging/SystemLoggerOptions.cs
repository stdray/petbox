using System.Collections.Immutable;
using System.Text.Json;
using PetBox.Log.Core.Data;
using PetBox.Log.Core.Models;

namespace PetBox.Log.Core.SelfLogging;

// One routing rule: events from a (SourceContext, EventId) pair go to Destination instead of the
// default self-log. Category is matched as a PREFIX (like SystemLoggerOptions.CategoryPrefix
// already does), not exact-equality — an ILogger<T> category is the type's full name
// (namespace.ClassName), so a namespace-shaped rule like "PetBox.Web.Logging" covers every logger
// under it without enumerating each class. EventId is exact. Deliberately NOT a regex/expression
// engine — spec self-telemetry-log-routing asks for a simple list of pairs, nothing more.
public sealed record SelfLogRoute
{
	public required string Category { get; init; }
	public required int EventId { get; init; }
	public required string Destination { get; init; }
}

public sealed record SystemLoggerOptions
{
	public string CategoryPrefix { get; init; } = "PetBox";
	public string ServiceKey { get; init; } = "petbox-web";
	public int QueueCapacity { get; init; } = 5_000;
	public int BatchSize { get; init; } = 200;
	public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);
	public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

	// The access-line events (RequestLoggingMiddleware, EventId 500-503) are the high-frequency
	// stream the spec forbids losing OR letting drown out the rest of the self-log: they route to
	// their own log (`access`) instead of `petbox`. Anything that matches no rule falls back to
	// `petbox` (LogNames.SelfLog), same as before this feature existed. Bound from config section
	// "SelfLogging:Routes" — a List<T>, not ImmutableArray<T>, because the IConfiguration binder
	// needs a mutable collection type to populate from a config array.
	public List<SelfLogRoute> Routes { get; init; } =
	[
		new() { Category = "PetBox.Web.Logging", EventId = 500, Destination = LogNames.AccessLog },
		new() { Category = "PetBox.Web.Logging", EventId = 501, Destination = LogNames.AccessLog },
		new() { Category = "PetBox.Web.Logging", EventId = 502, Destination = LogNames.AccessLog },
		new() { Category = "PetBox.Web.Logging", EventId = 503, Destination = LogNames.AccessLog },
	];

	// Stamped on every self-log event (App/Env/Ver/Sha/Host). `set` not `init` because
	// IOptions Configure lambdas in Program.cs mutate post-binding.
	public ImmutableDictionary<string, JsonElement> StaticProperties { get; set; } =
		ImmutableDictionary<string, JsonElement>.Empty;
}
