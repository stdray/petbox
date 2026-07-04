using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using PetBox.Log.Core.Tracing;
using ProtoSpan = OpenTelemetry.Proto.Trace.V1.Span;
using ProtoSpanKind = OpenTelemetry.Proto.Trace.V1.Span.Types.SpanKind;
using BoxSpanKind = PetBox.Log.Core.Tracing.SpanKind;

namespace PetBox.Web.Ingestion;

// OTLP Traces (HTTP/Protobuf) → SpanRecord[]. Resource + scope + span attributes merged
// into AttributesJson with resource-wins-on-collision.
//
// Invariant: proto DTOs never leak past this file. Everything downstream sees SpanRecord.
static class OtlpTracesParser
{
	public static OtlpTracesParseResult Parse(ReadOnlySpan<byte> protobuf)
	{
		ExportTraceServiceRequest request;
		try
		{
			request = ExportTraceServiceRequest.Parser.ParseFrom(protobuf);
		}
		catch (InvalidProtocolBufferException)
		{
			return OtlpTracesParseResult.Malformed;
		}

		var spans = new List<SpanRecord>();
		var errors = 0;

		foreach (var resourceSpans in request.ResourceSpans)
		{
			var resourceAttrs = FlattenAttributes(resourceSpans.Resource?.Attributes);
			foreach (var scopeSpans in resourceSpans.ScopeSpans)
			{
				var scopeAttrs = FlattenAttributes(scopeSpans.Scope?.Attributes);
				foreach (var protoSpan in scopeSpans.Spans)
				{
					if (TryConvert(protoSpan, resourceAttrs, scopeAttrs, out var span))
						spans.Add(span);
					else
						errors++;
				}
			}
		}

		return new OtlpTracesParseResult(spans, errors, IsMalformed: false);
	}

	static bool TryConvert(
		ProtoSpan protoSpan,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		ImmutableDictionary<string, JsonElement> scopeAttrs,
		out SpanRecord span)
	{
		span = null!;

		if (protoSpan.TraceId.IsEmpty || AllZero(protoSpan.TraceId.Span)) return false;
		if (protoSpan.SpanId.IsEmpty || AllZero(protoSpan.SpanId.Span)) return false;
		if (protoSpan.StartTimeUnixNano == 0) return false;

		// Merge order: span < scope < resource. Resource identity (service.name / host.name)
		// must win on collision.
		var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		foreach (var kv in FlattenAttributes(protoSpan.Attributes))
			attributes[kv.Key] = kv.Value;
		foreach (var kv in scopeAttrs)
			attributes[kv.Key] = kv.Value;
		foreach (var kv in resourceAttrs)
			attributes[kv.Key] = kv.Value;

		if (protoSpan.DroppedAttributesCount != 0)
			attributes["otlp_dropped_attributes"] = JsonNumber(protoSpan.DroppedAttributesCount);
		if (protoSpan.DroppedEventsCount != 0)
			attributes["otlp_dropped_events"] = JsonNumber(protoSpan.DroppedEventsCount);
		if (protoSpan.DroppedLinksCount != 0)
			attributes["otlp_dropped_links"] = JsonNumber(protoSpan.DroppedLinksCount);

		span = new SpanRecord
		{
			SpanId = Convert.ToHexStringLower(protoSpan.SpanId.Span),
			TraceId = Convert.ToHexStringLower(protoSpan.TraceId.Span),
			ParentSpanId = protoSpan.ParentSpanId.IsEmpty || AllZero(protoSpan.ParentSpanId.Span)
				? null
				: Convert.ToHexStringLower(protoSpan.ParentSpanId.Span),
			Name = protoSpan.Name,
			Kind = (int)MapKind(protoSpan.Kind),
			StartUnixNs = (long)protoSpan.StartTimeUnixNano,
			EndUnixNs = (long)(protoSpan.EndTimeUnixNano > protoSpan.StartTimeUnixNano ? protoSpan.EndTimeUnixNano : protoSpan.StartTimeUnixNano),
			StatusCode = protoSpan.Status is null ? (int)SpanStatusCode.Unset : (int)MapStatus(protoSpan.Status.Code),
			StatusDescription = protoSpan.Status?.Message is { Length: > 0 } msg ? msg : null,
			AttributesJson = JsonSerializer.Serialize(attributes),
			EventsJson = SerializeEvents(protoSpan.Events),
			LinksJson = SerializeLinks(protoSpan.Links),
		};
		return true;
	}

	static BoxSpanKind MapKind(ProtoSpanKind kind) => kind switch
	{
		ProtoSpanKind.Internal => BoxSpanKind.Internal,
		ProtoSpanKind.Server => BoxSpanKind.Server,
		ProtoSpanKind.Client => BoxSpanKind.Client,
		ProtoSpanKind.Producer => BoxSpanKind.Producer,
		ProtoSpanKind.Consumer => BoxSpanKind.Consumer,
		// OTLP spec says treat Unspecified as Internal.
		_ => BoxSpanKind.Internal,
	};

	static SpanStatusCode MapStatus(Status.Types.StatusCode code) => code switch
	{
		Status.Types.StatusCode.Unset => SpanStatusCode.Unset,
		Status.Types.StatusCode.Ok => SpanStatusCode.Ok,
		Status.Types.StatusCode.Error => SpanStatusCode.Error,
		_ => SpanStatusCode.Unset,
	};

	static string SerializeEvents(Google.Protobuf.Collections.RepeatedField<ProtoSpan.Types.Event> events)
	{
		if (events.Count == 0) return "[]";
		var list = new List<Dictionary<string, JsonElement>>(events.Count);
		foreach (var e in events)
		{
			var attrs = FlattenAttributes(e.Attributes);
			list.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["timestamp_unix_ns"] = JsonSerializer.SerializeToElement(e.TimeUnixNano),
				["name"] = JsonSerializer.SerializeToElement(e.Name),
				["attributes"] = JsonSerializer.SerializeToElement(attrs),
			});
		}
		return JsonSerializer.Serialize(list);
	}

	static string SerializeLinks(Google.Protobuf.Collections.RepeatedField<ProtoSpan.Types.Link> links)
	{
		if (links.Count == 0) return "[]";
		var list = new List<Dictionary<string, JsonElement>>(links.Count);
		foreach (var l in links)
		{
			var attrs = FlattenAttributes(l.Attributes);
			list.Add(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["trace_id"] = JsonSerializer.SerializeToElement(Convert.ToHexStringLower(l.TraceId.Span)),
				["span_id"] = JsonSerializer.SerializeToElement(Convert.ToHexStringLower(l.SpanId.Span)),
				["attributes"] = JsonSerializer.SerializeToElement(attrs),
			});
		}
		return JsonSerializer.Serialize(list);
	}

	static ImmutableDictionary<string, JsonElement> FlattenAttributes(
		Google.Protobuf.Collections.RepeatedField<KeyValue>? attributes)
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
			builder[names.Assign(kv.Key)] = AnyValueToJson(kv.Value);
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

	static bool AllZero(ReadOnlySpan<byte> bytes)
	{
		for (var i = 0; i < bytes.Length; i++)
			if (bytes[i] != 0) return false;
		return true;
	}

	static JsonElement JsonNumber(uint n) => JsonSerializer.SerializeToElement(n);
	static JsonElement JsonNull() => JsonSerializer.SerializeToElement<object?>(null);
}

sealed record OtlpTracesParseResult(
	IReadOnlyList<SpanRecord> Spans,
	int Errors,
	bool IsMalformed)
{
	public static readonly OtlpTracesParseResult Malformed = new([], 0, IsMalformed: true);
}
