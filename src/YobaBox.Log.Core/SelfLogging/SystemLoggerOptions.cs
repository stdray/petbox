using System.Collections.Immutable;
using System.Text.Json;
using YobaBox.Log.Core.Models;

namespace YobaBox.Log.Core.SelfLogging;

public sealed record SystemLoggerOptions
{
	public string CategoryPrefix { get; init; } = "YobaBox";
	public string ServiceKey { get; init; } = "yobabox-web";
	public int QueueCapacity { get; init; } = 5_000;
	public int BatchSize { get; init; } = 200;
	public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(2);
	public LogLevel MinimumLevel { get; init; } = LogLevel.Information;

	// Stamped on every self-log event (App/Env/Ver/Sha/Host). `set` not `init` because
	// IOptions Configure lambdas in Program.cs mutate post-binding.
	public ImmutableDictionary<string, JsonElement> StaticProperties { get; set; } =
		ImmutableDictionary<string, JsonElement>.Empty;
}
