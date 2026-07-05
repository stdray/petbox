using LinqToDB.Mapping;

namespace PetBox.Log.Core.Metrics;

// OTLP metric-type discriminator, stored as an int (MetricType column) the same way SpanKind/StatusCode
// are stored as ints on SpanRecord. Each arm decides which of the wide scalar / JSON-tail columns below
// carry meaning for a given point.
public enum MetricPointType
{
	Gauge = 0,
	Sum = 1,
	Histogram = 2,
	ExponentialHistogram = 3,
	Summary = 4,
}

// Numeric-discriminator → name mapping for the KQL engine's computed TypeName column — the metrics
// analog of SpanKindNames/SpanStatusNames (Kind/KindName on spans) and LogLevelNames (Level/LevelName
// on events). An out-of-range discriminator renders as "Unknown" rather than a bare number.
public static class MetricPointTypeNames
{
	public static string ToName(int type) => type switch
	{
		0 => "Gauge",
		1 => "Sum",
		2 => "Histogram",
		3 => "ExponentialHistogram",
		4 => "Summary",
		_ => "Unknown",
	};
}

// One OTLP metric data point, flattened into the per-log SQLite DB alongside Spans/LogEntries (single
// context — enables cross-signal KQL joins). Mirrors SpanRecord: wide, mostly-nullable scalar columns are
// effectively free in SQLite (unset trailing columns store at ~1 byte) and are directly KQL-addressable;
// only the inherently array-shaped fields go to JSON, the same way SpanRecord parks Events/Links as JSON.
[Table("MetricPoints")]
public sealed record MetricPointRecord
{
	[Column, PrimaryKey, Identity]
	public long Id { get; init; }

	[Column, NotNull]
	public string MetricName { get; init; } = string.Empty;

	// MetricPointType stored as int (0=Gauge, 1=Sum, 2=Histogram, 3=ExponentialHistogram, 4=Summary).
	[Column, NotNull]
	public int MetricType { get; init; }

	[Column]
	public string? Unit { get; init; }
	[Column]
	public string? Description { get; init; }

	[Column, NotNull]
	public long TimeUnixNs { get; init; }
	[Column]
	public long? StartUnixNs { get; init; }
	[Column]
	public int? Flags { get; init; }

	// Value oneof (Gauge/Sum) split into two arms so int64 exactness survives — never coerced through double.
	[Column]
	public double? ValueDouble { get; init; }
	[Column]
	public long? ValueLong { get; init; }

	[Column]
	public int? AggregationTemporality { get; init; }
	[Column]
	public bool? IsMonotonic { get; init; }

	// Histogram / ExponentialHistogram / Summary aggregate scalars.
	[Column]
	public long? Count { get; init; }
	[Column]
	public double? Sum { get; init; }
	[Column]
	public double? Min { get; init; }
	[Column]
	public double? Max { get; init; }

	// ExponentialHistogram scalars.
	[Column]
	public int? Scale { get; init; }
	[Column]
	public long? ZeroCount { get; init; }

	// Per-point attribute bag, spans-style flat dotted JSON object (same role as SpanRecord.AttributesJson).
	[Column, NotNull]
	public string AttributesJson { get; init; } = "{}";

	// JSON tail — the inherently array-shaped fields only. Nullable TEXT; a plain JSON blob here, no
	// KqlPropertyKeys normalization (that happens in the parser at the write boundary).
	[Column]
	public string? ExplicitBoundsJson { get; init; }   // Histogram: [double,…]
	[Column]
	public string? BucketCountsJson { get; init; }      // Histogram: [uint,…]
	[Column]
	public string? PositiveBucketsJson { get; init; }   // ExpHistogram: {offset,bucket_counts:[…]}
	[Column]
	public string? NegativeBucketsJson { get; init; }   // ExpHistogram: {offset,bucket_counts:[…]}
	[Column]
	public string? QuantileValuesJson { get; init; }    // Summary: [{quantile,value},…]
	[Column]
	public string? ExemplarsJson { get; init; }         // rare, all types: [{…}]
}
