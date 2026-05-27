using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using YobaBox.Log.Core.Models;
using BoxLogLevel = YobaBox.Log.Core.Models.LogLevel;

namespace YobaBox.Web.Ingestion;

// OTLP Logs (HTTP/Protobuf) → LogEntryCandidate batch.
//
// Invariant: proto DTOs never leak past this file. Everything downstream speaks LogEntryCandidate.
// TraceId/SpanId/EventId get folded into the Properties JSON (yobabox LogEntryCandidate has no
// first-class trace fields — see Models/LogEntryCandidate.cs).
static class OtlpLogsParser
{
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

		var messageTemplate = string.IsNullOrEmpty(record.EventName) ? body : record.EventName;

		var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

		// Attribute merge order: record < resource. Resource attributes describe deployment
		// identity (service.name, host.name, ...) and must win on collision — a record can't
		// override what container it came from.
		foreach (var kv in FlattenAttributes(record.Attributes))
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
			Message = body,
			MessageTemplate = messageTemplate,
			Exception = null,
			Properties = JsonSerializer.Serialize(properties),
		};
		return true;
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
		Google.Protobuf.Collections.RepeatedField<KeyValue>? attributes)
	{
		if (attributes is null || attributes.Count == 0)
			return ImmutableDictionary<string, JsonElement>.Empty;

		var builder = ImmutableDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
		foreach (var kv in attributes)
		{
			if (string.IsNullOrEmpty(kv.Key)) continue;
			builder[kv.Key] = AnyValueToJson(kv.Value);
		}
		return builder.ToImmutable();
	}

	static JsonElement AnyValueToJson(AnyValue? value)
	{
		if (value is null) return JsonNull();
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
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

	static JsonElement JsonString(string s) => JsonSerializer.SerializeToElement(s);
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
