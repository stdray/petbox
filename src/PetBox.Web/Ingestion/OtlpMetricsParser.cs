using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using PetBox.Core.Json;
using PetBox.Log.Core.Metrics;

namespace PetBox.Web.Ingestion;

// OTLP Metrics (HTTP/Protobuf) → MetricPointRecord[]. Resource + scope + point attributes merged
// into AttributesJson with resource-wins-on-collision, exactly like OtlpTracesParser.
//
// Each Metric carries name/unit/description plus a `data` oneof selecting one of five point types
// (Gauge/Sum/Histogram/ExponentialHistogram/Summary). We walk that type's data_points and emit one
// MetricPointRecord per point, filling only the wide columns / JSON tails meaningful to that type
// (the wide-table design leaves the rest null).
//
// Invariant: proto DTOs never leak past this file. Everything downstream sees MetricPointRecord.
static class OtlpMetricsParser
{
	public static OtlpMetricsParseResult Parse(ReadOnlySpan<byte> protobuf)
	{
		ExportMetricsServiceRequest request;
		try
		{
			request = ExportMetricsServiceRequest.Parser.ParseFrom(protobuf);
		}
		catch (InvalidProtocolBufferException)
		{
			return OtlpMetricsParseResult.Malformed;
		}

		var points = new List<MetricPointRecord>();
		var errors = 0;

		foreach (var resourceMetrics in request.ResourceMetrics)
		{
			var resourceAttrs = FlattenAttributes(resourceMetrics.Resource?.Attributes);
			foreach (var scopeMetrics in resourceMetrics.ScopeMetrics)
			{
				var scopeAttrs = FlattenAttributes(scopeMetrics.Scope?.Attributes);
				foreach (var metric in scopeMetrics.Metrics)
					EmitMetric(metric, resourceAttrs, scopeAttrs, points, ref errors);
			}
		}

		return new OtlpMetricsParseResult(points, errors, IsMalformed: false);
	}

	static void EmitMetric(
		Metric metric,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		ImmutableDictionary<string, JsonElement> scopeAttrs,
		List<MetricPointRecord> points,
		ref int errors)
	{
		switch (metric.DataCase)
		{
			case Metric.DataOneofCase.Gauge:
				foreach (var dp in metric.Gauge.DataPoints)
					Add(TryNumber(metric, MetricPointType.Gauge, dp, aggTemporality: null, isMonotonic: null,
						resourceAttrs, scopeAttrs), points, ref errors);
				break;

			case Metric.DataOneofCase.Sum:
				var sum = metric.Sum;
				foreach (var dp in sum.DataPoints)
					Add(TryNumber(metric, MetricPointType.Sum, dp, (int)sum.AggregationTemporality, sum.IsMonotonic,
						resourceAttrs, scopeAttrs), points, ref errors);
				break;

			case Metric.DataOneofCase.Histogram:
				var hist = metric.Histogram;
				foreach (var dp in hist.DataPoints)
					Add(TryHistogram(metric, dp, (int)hist.AggregationTemporality, resourceAttrs, scopeAttrs),
						points, ref errors);
				break;

			case Metric.DataOneofCase.ExponentialHistogram:
				var exp = metric.ExponentialHistogram;
				foreach (var dp in exp.DataPoints)
					Add(TryExponentialHistogram(metric, dp, (int)exp.AggregationTemporality, resourceAttrs, scopeAttrs),
						points, ref errors);
				break;

			case Metric.DataOneofCase.Summary:
				foreach (var dp in metric.Summary.DataPoints)
					Add(TrySummary(metric, dp, resourceAttrs, scopeAttrs), points, ref errors);
				break;

			// Metric with no data set (DataOneofCase.None) — nothing to emit, not an error.
			default:
				break;
		}
	}

	static void Add(MetricPointRecord? record, List<MetricPointRecord> points, ref int errors)
	{
		if (record is null) errors++;
		else points.Add(record);
	}

	// Gauge / Sum share NumberDataPoint. The value oneof is split across two columns so int64
	// exactness survives — an int point NEVER goes through ValueDouble.
	static MetricPointRecord? TryNumber(
		Metric metric,
		MetricPointType type,
		NumberDataPoint dp,
		int? aggTemporality,
		bool? isMonotonic,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		ImmutableDictionary<string, JsonElement> scopeAttrs)
	{
		if (dp.TimeUnixNano == 0) return null;
		return NewRecord(metric, type, dp.TimeUnixNano, dp.StartTimeUnixNano, dp.Flags, dp.Attributes,
			resourceAttrs, scopeAttrs) with
		{
			ValueDouble = dp.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble ? dp.AsDouble : null,
			ValueLong = dp.ValueCase == NumberDataPoint.ValueOneofCase.AsInt ? dp.AsInt : null,
			AggregationTemporality = aggTemporality,
			IsMonotonic = isMonotonic,
			ExemplarsJson = SerializeExemplars(dp.Exemplars),
		};
	}

	static MetricPointRecord? TryHistogram(
		Metric metric,
		HistogramDataPoint dp,
		int aggTemporality,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		ImmutableDictionary<string, JsonElement> scopeAttrs)
	{
		if (dp.TimeUnixNano == 0) return null;
		return NewRecord(metric, MetricPointType.Histogram, dp.TimeUnixNano, dp.StartTimeUnixNano, dp.Flags,
			dp.Attributes, resourceAttrs, scopeAttrs) with
		{
			Count = (long)dp.Count,
			Sum = dp.HasSum ? dp.Sum : null,
			Min = dp.HasMin ? dp.Min : null,
			Max = dp.HasMax ? dp.Max : null,
			AggregationTemporality = aggTemporality,
			ExplicitBoundsJson = dp.ExplicitBounds.Count > 0 ? JsonSerializer.Serialize(dp.ExplicitBounds) : null,
			BucketCountsJson = dp.BucketCounts.Count > 0 ? JsonSerializer.Serialize(dp.BucketCounts) : null,
			ExemplarsJson = SerializeExemplars(dp.Exemplars),
		};
	}

	static MetricPointRecord? TryExponentialHistogram(
		Metric metric,
		ExponentialHistogramDataPoint dp,
		int aggTemporality,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		ImmutableDictionary<string, JsonElement> scopeAttrs)
	{
		if (dp.TimeUnixNano == 0) return null;
		return NewRecord(metric, MetricPointType.ExponentialHistogram, dp.TimeUnixNano, dp.StartTimeUnixNano,
			dp.Flags, dp.Attributes, resourceAttrs, scopeAttrs) with
		{
			Count = (long)dp.Count,
			Sum = dp.HasSum ? dp.Sum : null,
			Min = dp.HasMin ? dp.Min : null,
			Max = dp.HasMax ? dp.Max : null,
			Scale = dp.Scale,
			ZeroCount = (long)dp.ZeroCount,
			AggregationTemporality = aggTemporality,
			PositiveBucketsJson = SerializeBuckets(dp.Positive),
			NegativeBucketsJson = SerializeBuckets(dp.Negative),
			ExemplarsJson = SerializeExemplars(dp.Exemplars),
		};
	}

	static MetricPointRecord? TrySummary(
		Metric metric,
		SummaryDataPoint dp,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		ImmutableDictionary<string, JsonElement> scopeAttrs)
	{
		if (dp.TimeUnixNano == 0) return null;
		return NewRecord(metric, MetricPointType.Summary, dp.TimeUnixNano, dp.StartTimeUnixNano, dp.Flags,
			dp.Attributes, resourceAttrs, scopeAttrs) with
		{
			Count = (long)dp.Count,
			Sum = dp.Sum,
			QuantileValuesJson = SerializeQuantiles(dp.QuantileValues),
		};
	}

	// Common columns shared by every point type: metric metadata + timestamps + merged attributes.
	static MetricPointRecord NewRecord(
		Metric metric,
		MetricPointType type,
		ulong timeUnixNano,
		ulong startTimeUnixNano,
		uint flags,
		Google.Protobuf.Collections.RepeatedField<KeyValue> pointAttrs,
		ImmutableDictionary<string, JsonElement> resourceAttrs,
		ImmutableDictionary<string, JsonElement> scopeAttrs)
	{
		// Merge order: point < scope < resource. Resource identity (service.name / host.name)
		// must win on collision.
		var attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		foreach (var kv in FlattenAttributes(pointAttrs))
			attributes[kv.Key] = kv.Value;
		foreach (var kv in scopeAttrs)
			attributes[kv.Key] = kv.Value;
		foreach (var kv in resourceAttrs)
			attributes[kv.Key] = kv.Value;

		return new MetricPointRecord
		{
			MetricName = metric.Name,
			MetricType = (int)type,
			Unit = string.IsNullOrEmpty(metric.Unit) ? null : metric.Unit,
			Description = string.IsNullOrEmpty(metric.Description) ? null : metric.Description,
			TimeUnixNs = (long)timeUnixNano,
			StartUnixNs = startTimeUnixNano != 0 ? (long)startTimeUnixNano : null,
			Flags = flags != 0 ? (int)flags : null,
			AttributesJson = JsonSerializer.Serialize(attributes, PetBoxJsonEncoder.SharedOptions),
		};
	}

	static string? SerializeBuckets(ExponentialHistogramDataPoint.Types.Buckets? buckets)
	{
		if (buckets is null) return null;
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartObject();
			writer.WriteNumber("offset", buckets.Offset);
			writer.WriteStartArray("bucket_counts");
			foreach (var c in buckets.BucketCounts)
				writer.WriteNumberValue(c);
			writer.WriteEndArray();
			writer.WriteEndObject();
		}
		return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	static string? SerializeQuantiles(
		Google.Protobuf.Collections.RepeatedField<SummaryDataPoint.Types.ValueAtQuantile> quantiles)
	{
		if (quantiles.Count == 0) return null;
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartArray();
			foreach (var q in quantiles)
			{
				writer.WriteStartObject();
				writer.WriteNumber("quantile", q.Quantile);
				writer.WriteNumber("value", q.Value);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		}
		return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
	}

	// Exemplars are rare and their shape varies by point value type; keep the projection small —
	// time, value (double or int arm), and the exemplar's own trace/span linkage when present.
	static string? SerializeExemplars(Google.Protobuf.Collections.RepeatedField<Exemplar> exemplars)
	{
		if (exemplars.Count == 0) return null;
		var buffer = new ArrayBufferWriter<byte>();
		using (var writer = new Utf8JsonWriter(buffer))
		{
			writer.WriteStartArray();
			foreach (var e in exemplars)
			{
				writer.WriteStartObject();
				writer.WriteNumber("time_unix_ns", e.TimeUnixNano);
				switch (e.ValueCase)
				{
					case Exemplar.ValueOneofCase.AsDouble: writer.WriteNumber("value_double", e.AsDouble); break;
					case Exemplar.ValueOneofCase.AsInt: writer.WriteNumber("value_long", e.AsInt); break;
					default: break;
				}
				if (!e.TraceId.IsEmpty && !AllZero(e.TraceId.Span))
					writer.WriteString("trace_id", Convert.ToHexStringLower(e.TraceId.Span));
				if (!e.SpanId.IsEmpty && !AllZero(e.SpanId.Span))
					writer.WriteString("span_id", Convert.ToHexStringLower(e.SpanId.Span));
				writer.WriteEndObject();
			}
			writer.WriteEndArray();
		}
		return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
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

	// json-encoder-shared-globally: see OtlpTracesParser.AnyValueWriterOptions — a bare
	// `new Utf8JsonWriter(buffer)` would bake a Cyrillic attribute value into \uXXXX here, before
	// it ever reaches an outer options-aware Serialize call.
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

	static bool AllZero(ReadOnlySpan<byte> bytes)
	{
		for (var i = 0; i < bytes.Length; i++)
			if (bytes[i] != 0) return false;
		return true;
	}

	static JsonElement JsonNull() => JsonSerializer.SerializeToElement<object?>(null);
}

sealed record OtlpMetricsParseResult(
	IReadOnlyList<MetricPointRecord> Points,
	int Errors,
	bool IsMalformed)
{
	public static readonly OtlpMetricsParseResult Malformed = new([], 0, IsMalformed: true);
}
