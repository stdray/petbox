using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PetBox.Log.Core.Models;
using BoxLogLevel = PetBox.Log.Core.Models.LogLevel;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace PetBox.Log.Core.SelfLogging;

sealed class SystemLogger : ILogger
{
	readonly string _category;
	readonly SystemLoggerOptions _options;
	readonly ChannelWriter<LogEntryCandidate> _writer;
	readonly TimeProvider _time;

	public SystemLogger(string category, SystemLoggerOptions options, ChannelWriter<LogEntryCandidate> writer, TimeProvider time)
	{
		_category = category;
		_options = options;
		_writer = writer;
		_time = time;
	}

	public bool IsEnabled(MelLogLevel logLevel) =>
		logLevel != MelLogLevel.None
		&& _category.StartsWith(_options.CategoryPrefix, StringComparison.Ordinal)
		&& logLevel >= ToMelLevel(_options.MinimumLevel);

	public void Log<TState>(
		MelLogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		if (!IsEnabled(logLevel))
			return;

		ArgumentNullException.ThrowIfNull(formatter);

		var message = formatter(state, exception);
		var template = ExtractTemplate(state) ?? message;

		// Activity.Current — set by AspNetCoreInstrumentation root span; domain code extends
		// via ActivitySource.StartActivity. Reading directly here bypasses MEL scope plumbing
		// (SystemLogger returns null from BeginScope) but stamps the same TraceId/SpanId
		// ActivityTrackingOptions would produce.
		var activity = Activity.Current;
		var traceId = activity?.TraceId.ToHexString();
		var spanId = activity?.SpanId.ToHexString();

		var props = new Dictionary<string, JsonElement>(_options.StaticProperties.Count + 4, StringComparer.Ordinal);
		foreach (var kv in _options.StaticProperties)
			props[kv.Key] = kv.Value;
		props["SourceContext"] = JsonSerializer.SerializeToElement(_category);
		if (!string.IsNullOrEmpty(traceId) && traceId != "00000000000000000000000000000000")
			props["TraceId"] = JsonSerializer.SerializeToElement(traceId);
		if (!string.IsNullOrEmpty(spanId) && spanId != "0000000000000000")
			props["SpanId"] = JsonSerializer.SerializeToElement(spanId);
		if (eventId.Id != 0)
			props["EventId"] = JsonSerializer.SerializeToElement(eventId.Id);

		// Best-effort: extract named template arguments (excluding {OriginalFormat}) into properties.
		if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
		{
			foreach (var kv in kvs)
			{
				if (kv.Key == "{OriginalFormat}") continue;
				if (kv.Value is null) continue;
				try { props[kv.Key] = JsonSerializer.SerializeToElement(kv.Value); }
				catch (NotSupportedException)
				{
					props[kv.Key] = JsonSerializer.SerializeToElement(kv.Value.ToString());
				}
			}
		}

		var candidate = new LogEntryCandidate
		{
			ServiceKey = _options.ServiceKey,
			Timestamp = _time.GetUtcNow().UtcDateTime,
			Level = FromMelLevel(logLevel),
			Message = message,
			MessageTemplate = template,
			Exception = exception?.ToString(),
			// The shared WRITE boundary (key normalization + collision policy), NOT raw serialization —
			// self-log properties must be as KQL-addressable as ingested ones.
			Properties = PropertiesJsonSerializer.Serialize(props),
		};

		_writer.TryWrite(candidate);
	}

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	static string? ExtractTemplate<TState>(TState state)
	{
		if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
		{
			foreach (var kv in kvs)
				if (kv.Key == "{OriginalFormat}" && kv.Value is string s)
					return s;
		}
		return null;
	}

	static MelLogLevel ToMelLevel(BoxLogLevel level) => level switch
	{
		BoxLogLevel.Verbose => MelLogLevel.Trace,
		BoxLogLevel.Debug => MelLogLevel.Debug,
		BoxLogLevel.Information => MelLogLevel.Information,
		BoxLogLevel.Warning => MelLogLevel.Warning,
		BoxLogLevel.Error => MelLogLevel.Error,
		BoxLogLevel.Fatal => MelLogLevel.Critical,
		_ => MelLogLevel.Information,
	};

	static BoxLogLevel FromMelLevel(MelLogLevel level) => level switch
	{
		MelLogLevel.Trace => BoxLogLevel.Verbose,
		MelLogLevel.Debug => BoxLogLevel.Debug,
		MelLogLevel.Information => BoxLogLevel.Information,
		MelLogLevel.Warning => BoxLogLevel.Warning,
		MelLogLevel.Error => BoxLogLevel.Error,
		MelLogLevel.Critical => BoxLogLevel.Fatal,
		_ => BoxLogLevel.Information,
	};
}
