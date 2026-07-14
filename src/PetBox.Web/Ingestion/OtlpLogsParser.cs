using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using PetBox.Core.Json;
using PetBox.Log.Core.Models;
using BoxLogLevel = PetBox.Log.Core.Models.LogLevel;

namespace PetBox.Web.Ingestion;

// OTLP Logs (HTTP/Protobuf) → LogEntryCandidate batch.
//
// Invariant: proto DTOs never leak past this file. Everything downstream speaks LogEntryCandidate.
// TraceId/SpanId/EventId get folded into the Properties JSON (petbox LogEntryCandidate has no
// first-class trace fields — see Models/LogEntryCandidate.cs).
//
// CLEF PARITY (the fidelity contract of compat-ingest — a stock OTLP client must land the same
// entry a Seq/CLEF client does):
//   @x  ← the OTel `exception.type` / `exception.message` / `exception.stacktrace` attributes,
//         recomposed into the single Exception string CLEF carries (see ComposeException).
//   @mt ← the message template. OTLP has no template field; the .NET exporter ships it as the
//         `{OriginalFormat}` attribute (OtlpLogRecordTransformer special-cases that key).
// Both are LIFTED out of the attribute bag into their first-class fields and NOT also mirrored
// into Properties — CLEF does not put @x/@mt in the property bag either.
//
// KNOWN LIMIT — the rendered message can be missing, and it is not ours to invent:
//   the .NET exporter emits `{OriginalFormat}` only when it also renders the body
//   (IncludeFormattedMessage=true). With the default IncludeFormattedMessage=false the exporter
//   puts the TEMPLATE ITSELF in Body and sends no `{OriginalFormat}` attribute — so Message and
//   MessageTemplate are both the template and the rendered text does not exist on the wire.
//   Non-.NET SDKs (python/go/java) have no message-template concept at all: no template attribute
//   ever arrives and MessageTemplate falls back to Body (= Message). Template-hash grouping is
//   therefore exact for .NET-with-formatted-message, degenerate-but-stable otherwise. We do not
//   reverse-engineer a template out of a rendered string.
static class OtlpLogsParser
{
	// OTel semantic conventions for an exception recorded on a log record.
	const string ExceptionType = "exception.type";
	const string ExceptionMessage = "exception.message";
	const string ExceptionStacktrace = "exception.stacktrace";

	// The .NET convention for the Microsoft.Extensions.Logging message template.
	const string OriginalFormat = "{OriginalFormat}";

	public static OtlpLogsParseResult Parse(ReadOnlySpan<byte> protobuf, string serviceKey)
	{
		ExportLogsServiceRequest request;
		try
		{
			request = ExportLogsServiceRequest.Parser.ParseFrom(protobuf);
		}
		catch (InvalidProtocolBufferException)
		{
			return OtlpLogsParseResult.Malformed;
		}

		var candidates = new List<LogEntryCandidate>();
		var errors = 0;

		foreach (var resourceLogs in request.ResourceLogs)
		{
			var resourceAttrs = FlattenAttributes(resourceLogs.Resource?.Attributes);
			foreach (var scopeLogs in resourceLogs.ScopeLogs)
			{
				foreach (var record in scopeLogs.LogRecords)
				{
					if (TryConvert(record, resourceAttrs, serviceKey, out var candidate))
						candidates.Add(candidate);
					else
						errors++;
				}
			}
		}

		return new OtlpLogsParseResult(candidates, errors, IsMalformed: false);
	}

	static bool TryConvert(
		LogRecord record,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		string serviceKey,
		out LogEntryCandidate candidate)
	{
		candidate = null!;

		var unixNs = record.TimeUnixNano != 0 ? record.TimeUnixNano : record.ObservedTimeUnixNano;
		if (unixNs == 0) return false;

		var timestampMs = (long)(unixNs / 1_000_000);
		var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime;

		var level = MapSeverity(record.SeverityNumber);
		var body = FormatBody(record.Body);

		// Template precedence: the exporter's explicit template ({OriginalFormat}) beats EventName
		// (an OTel event NAME — "device.app.lifecycle" — not a template, but the only pre-existing
		// signal here), which beats the rendered body. See the KNOWN LIMIT note on the class.
		var template = StringAttribute(record.Attributes, OriginalFormat)
			?? (string.IsNullOrEmpty(record.EventName) ? null : record.EventName);
		var messageTemplate = string.IsNullOrEmpty(template) ? body : template;
		// CLEF: an empty @m falls back to @mt. Same here — a body-less record with a template
		// still has a message.
		var message = string.IsNullOrEmpty(body) ? messageTemplate : body;

		var exception = ComposeException(record.Attributes);

		var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

		// Attribute merge order: record < resource. Resource attributes describe deployment
		// identity (service.name, host.name, ...) and must win on collision — a record can't
		// override what container it came from.
		foreach (var kv in FlattenAttributes(record.Attributes, LiftedKeys))
			properties[kv.Key] = kv.Value;
		foreach (var kv in resourceAttrs)
			properties[kv.Key] = kv.Value;

		if (!string.IsNullOrEmpty(record.SeverityText))
			properties["severity_text"] = JsonString(record.SeverityText);
		if (record.DroppedAttributesCount != 0)
			properties["otlp_dropped"] = JsonNumber(record.DroppedAttributesCount);
		if (record.Flags != 0)
			properties["otlp_flags"] = JsonNumber(record.Flags);

		var traceId = HexOrNull(record.TraceId.Span);
		if (traceId is not null)
			properties["TraceId"] = JsonString(traceId);
		var spanId = HexOrNull(record.SpanId.Span);
		if (spanId is not null)
			properties["SpanId"] = JsonString(spanId);

		candidate = new LogEntryCandidate
		{
			ServiceKey = serviceKey,
			Timestamp = timestamp,
			Level = level,
			Message = message,
			MessageTemplate = messageTemplate,
			Exception = exception,
			Properties = JsonSerializer.Serialize(properties, PetBoxJsonEncoder.SharedOptions),
		};
		return true;
	}

	// The four attribute keys promoted to first-class LogEntryCandidate fields — kept OUT of the
	// property bag so the entry looks exactly like the CLEF one (no exception.* duplicate of @x).
	static readonly ImmutableHashSet<string> LiftedKeys = ImmutableHashSet.Create(
		StringComparer.Ordinal, ExceptionType, ExceptionMessage, ExceptionStacktrace, OriginalFormat);

	// exception.{type,message,stacktrace} → the ONE string CLEF's @x carries (which, from a Serilog/
	// Seq client, is `Exception.ToString()`: "Type: message\n   at frame…").
	//
	// The OTel .NET SDK already sets exception.stacktrace to ex.ToString(), so its stacktrace ALREADY
	// starts with "Type: message" — blindly prepending the header would double it. Other SDKs (python,
	// go) send a bare traceback there. So: emit the header, and append the stacktrace only where it is
	// not already carrying it. A record with none of the three keys keeps Exception = null.
	static string? ComposeException(Google.Protobuf.Collections.RepeatedField<KeyValue>? attributes)
	{
		var type = StringAttribute(attributes, ExceptionType);
		var message = StringAttribute(attributes, ExceptionMessage);
		var stack = StringAttribute(attributes, ExceptionStacktrace);
		if (type is null && message is null && stack is null) return null;

		var header = (type, message) switch
		{
			(not null, not null) => $"{type}: {message}",
			(not null, null) => type,
			(null, not null) => message,
			_ => null,
		};

		if (stack is null) return header;
		if (header is null) return stack;
		return stack.StartsWith(header, StringComparison.Ordinal) ? stack : $"{header}{Environment.NewLine}{stack}";
	}

	// Raw (un-normalized) lookup of a string-valued attribute: the lifted keys are matched on the
	// wire spelling ("{OriginalFormat}", "exception.type"), BEFORE KqlPropertyKeys rewrites them for
	// storage — normalization is a property-bag concern and these never reach the bag.
	static string? StringAttribute(Google.Protobuf.Collections.RepeatedField<KeyValue>? attributes, string key)
	{
		if (attributes is null) return null;
		foreach (var kv in attributes)
			if (string.Equals(kv.Key, key, StringComparison.Ordinal)
				&& kv.Value?.ValueCase == AnyValue.ValueOneofCase.StringValue
				&& !string.IsNullOrEmpty(kv.Value.StringValue))
				return kv.Value.StringValue;
		return null;
	}

	// OTel SeverityNumber is a 1-24 ladder (4 steps per level). Map to the 6-level
	// CLEF ladder by integer division. Unspecified (0) defaults to Information — matches what
	// python-logging and log4j do when severity is unset.
	static BoxLogLevel MapSeverity(SeverityNumber severity) => severity switch
	{
		>= SeverityNumber.Trace and <= SeverityNumber.Trace4 => BoxLogLevel.Verbose,
		>= SeverityNumber.Debug and <= SeverityNumber.Debug4 => BoxLogLevel.Debug,
		>= SeverityNumber.Info and <= SeverityNumber.Info4 => BoxLogLevel.Information,
		>= SeverityNumber.Warn and <= SeverityNumber.Warn4 => BoxLogLevel.Warning,
		>= SeverityNumber.Error and <= SeverityNumber.Error4 => BoxLogLevel.Error,
		>= SeverityNumber.Fatal and <= SeverityNumber.Fatal4 => BoxLogLevel.Fatal,
		_ => BoxLogLevel.Information,
	};

	static string FormatBody(AnyValue? body)
	{
		if (body is null) return string.Empty;
		return body.ValueCase switch
		{
			AnyValue.ValueOneofCase.StringValue => body.StringValue,
			AnyValue.ValueOneofCase.BoolValue => body.BoolValue ? "true" : "false",
			AnyValue.ValueOneofCase.IntValue => body.IntValue.ToString(CultureInfo.InvariantCulture),
			AnyValue.ValueOneofCase.DoubleValue => body.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
			AnyValue.ValueOneofCase.BytesValue => Convert.ToBase64String(body.BytesValue.Span),
			AnyValue.ValueOneofCase.ArrayValue or AnyValue.ValueOneofCase.KvlistValue =>
				AnyValueToJson(body).GetRawText(),
			_ => string.Empty,
		};
	}

	static ImmutableDictionary<string, JsonElement> FlattenAttributes(
		Google.Protobuf.Collections.RepeatedField<KeyValue>? attributes,
		ImmutableHashSet<string>? skip = null)
	{
		if (attributes is null || attributes.Count == 0)
			return ImmutableDictionary<string, JsonElement>.Empty;

		var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		// WRITE-boundary key normalization (KqlPropertyKeys): stored attribute keys are always
		// KQL-addressable — safe inside a quoted JSON-path label — matching the search boundary. The
		// allocator suffixes DISTINCT originals that collide on one normalized name ('_2', '_3', …)
		// instead of silently last-wins-dropping them; a repeated identical key stays last-wins.
		var names = new PetBox.Log.Core.Query.KqlPropertyKeys.NameAllocator();
		foreach (var kv in attributes)
		{
			if (string.IsNullOrEmpty(kv.Key)) continue;
			if (skip is not null && skip.Contains(kv.Key)) continue;
			builder[names.Assign(kv.Key)] = AnyValueToJson(kv.Value);
		}
		return builder.ToImmutable();
	}

	// json-encoder-shared-globally: see OtlpTracesParser.AnyValueWriterOptions — a bare
	// `new Utf8JsonWriter(buffer)` would bake a Cyrillic attribute/body value into \uXXXX here
	// (GetRawText() below then hands that already-escaped text straight into LogEntryCandidate.
	// Message for an array/kvlist body — a directly human-facing field).
	static readonly JsonWriterOptions AnyValueWriterOptions = new() { Encoder = PetBoxJsonEncoder.Relaxed };

	static JsonElement AnyValueToJson(AnyValue? value)
	{
		if (value is null) return JsonNull();
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer, AnyValueWriterOptions))
			WriteAnyValue(writer, value);
		using var doc = JsonDocument.Parse(buffer.WrittenMemory);
		return doc.RootElement.Clone();
	}

	static void WriteAnyValue(Utf8JsonWriter writer, AnyValue value)
	{
		switch (value.ValueCase)
		{
			case AnyValue.ValueOneofCase.StringValue: writer.WriteStringValue(value.StringValue); break;
			case AnyValue.ValueOneofCase.BoolValue: writer.WriteBooleanValue(value.BoolValue); break;
			case AnyValue.ValueOneofCase.IntValue: writer.WriteNumberValue(value.IntValue); break;
			case AnyValue.ValueOneofCase.DoubleValue: writer.WriteNumberValue(value.DoubleValue); break;
			case AnyValue.ValueOneofCase.BytesValue: writer.WriteStringValue(Convert.ToBase64String(value.BytesValue.Span)); break;
			case AnyValue.ValueOneofCase.ArrayValue:
				writer.WriteStartArray();
				foreach (var item in value.ArrayValue.Values)
					WriteAnyValue(writer, item);
				writer.WriteEndArray();
				break;
			case AnyValue.ValueOneofCase.KvlistValue:
				writer.WriteStartObject();
				foreach (var kv in value.KvlistValue.Values)
				{
					writer.WritePropertyName(kv.Key);
					WriteAnyValue(writer, kv.Value);
				}
				writer.WriteEndObject();
				break;
			default: writer.WriteNullValue(); break;
		}
	}

	// trace_id is 16 bytes, span_id is 8. OTel treats all-zero as "absent" (traceparent
	// header convention) — emitting hex would pollute downstream joins with a magic constant.
	static string? HexOrNull(ReadOnlySpan<byte> bytes)
	{
		if (bytes.IsEmpty) return null;
		for (var i = 0; i < bytes.Length; i++)
			if (bytes[i] != 0)
				return Convert.ToHexStringLower(bytes);
		return null;
	}

	static JsonElement JsonString(string s) => JsonSerializer.SerializeToElement(s, PetBoxJsonEncoder.SharedOptions);
	static JsonElement JsonNumber(uint n) => JsonSerializer.SerializeToElement(n);
	static JsonElement JsonNull() => JsonSerializer.SerializeToElement<object?>(null);
}

sealed record OtlpLogsParseResult(
	IReadOnlyList<LogEntryCandidate> Candidates,
	int Errors,
	bool IsMalformed)
{
	public static readonly OtlpLogsParseResult Malformed = new([], 0, IsMalformed: true);
}
