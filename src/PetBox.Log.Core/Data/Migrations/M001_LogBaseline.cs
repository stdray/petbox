using FluentMigrator;

namespace PetBox.Log.Core.Data.Migrations;

// BASELINE of a per-log telemetry file (logs/{project}/{log}.db): the three OTLP-shaped signal
// tables — LogEntries (events), Spans (traces), MetricPoints (metrics) — and their read indexes
// (time-ordered scans + the KQL query paths).
//
// WHY EVERY OBJECT IS UNDER A `Schema...Exists()` GUARD — read before copying this pattern:
// this tier had NO migrations at all until now; the schema was created at runtime by hand-written
// `CREATE TABLE IF NOT EXISTS` DDL (LogSchema.Ensure), so every log file on a live deployment
// ALREADY has these tables and NO VersionInfo. A bare `Create.Table` would blow up with "table
// already exists" on all of them. The guards let this migration ADOPT what is there — rows
// (millions of log lines and spans) untouched — and create only what is missing, so an adopted
// file and a fresh file converge on the same schema. Same pardon, same reasoning as
// PetBox.Sessions M007_SearchCursorTables.
//
// THE GUARDS ARE PER OBJECT, NOT PER FILE, AND THAT IS LOAD-BEARING: the three tables did not
// arrive together. LogEntries came first, then Spans, then MetricPoints, and each ships its own
// indexes — a live file that predates a signal has the older tables and lacks the newer one
// outright (on 2026-07-11: 8 of the 9 live log files carry all three tables; logs/$system.db,
// born before the metrics work, has LogEntries + Spans and NO MetricPoints). So "the file exists"
// says nothing about WHICH objects it has; each one is adopted or created on its own.
//
// THIS GUARD IS NOT A LICENCE. It is legal in a BASELINE that adopts pre-migration files. Every
// later migration in this tier is written with a plain typed `Create.Table` / `Alter.Table`: from
// M002 on, VersionInfo is authoritative and a tolerant DDL could only hide drift.
[Migration(1, "Adopt/create the log entry, span and metric-point tables")]
public sealed class M001_LogBaseline : Migration
{
	public override void Up()
	{
		if (!Schema.Table("LogEntries").Exists())
			Create.Table("LogEntries")
				// `.Nullable()` on the identity PK looks wrong and is deliberate (same trick as
				// Sessions M007): without it FluentMigrator emits `INTEGER NOT NULL PRIMARY KEY
				// AUTOINCREMENT`, while the live files — and SQLite's own rowid-alias idiom — carry
				// `INTEGER PRIMARY KEY AUTOINCREMENT`. The NOT NULL is a no-op on a rowid alias, but
				// it shows up in PRAGMA table_info, which would leave a FRESH file permanently
				// different in shape from an ADOPTED one. Same for MetricPoints below.
				.WithColumn("Id").AsInt64().Nullable().PrimaryKey().Identity()
				.WithColumn("ServiceKey").AsString().NotNullable()
				.WithColumn("TimestampMs").AsInt64().NotNullable()
				.WithColumn("Level").AsInt32().NotNullable()
				.WithColumn("Message").AsString().NotNullable()
				.WithColumn("MessageTemplate").AsString().NotNullable()
				.WithColumn("Exception").AsString().Nullable()
				.WithColumn("PropertiesJson").AsString().NotNullable().WithDefaultValue("{}")
				// The stable hash of MessageTemplate — how the UI groups "the same message".
				.WithColumn("TemplateHash").AsInt64().NotNullable().WithDefaultValue(0);

		// The read paths: "this service, newest first", "everything, newest first", "errors only".
		CreateIndexIfMissing("IX_LogEntries_ServiceKey_TimestampMs", "LogEntries",
			() => Create.Index("IX_LogEntries_ServiceKey_TimestampMs").OnTable("LogEntries")
				.OnColumn("ServiceKey").Ascending()
				.OnColumn("TimestampMs").Descending());

		CreateIndexIfMissing("IX_LogEntries_TimestampMs", "LogEntries",
			() => Create.Index("IX_LogEntries_TimestampMs").OnTable("LogEntries")
				.OnColumn("TimestampMs").Descending());

		CreateIndexIfMissing("IX_LogEntries_Level", "LogEntries",
			() => Create.Index("IX_LogEntries_Level").OnTable("LogEntries")
				.OnColumn("Level").Ascending());

		if (!Schema.Table("Spans").Exists())
			Create.Table("Spans")
				// A TEXT PRIMARY KEY, so (unlike an INTEGER PK) it is not a rowid alias and SQLite
				// does NOT imply NOT NULL on it — `.Nullable()` is what the live files actually have.
				.WithColumn("SpanId").AsString().Nullable().PrimaryKey()
				.WithColumn("TraceId").AsString().NotNullable()
				.WithColumn("ParentSpanId").AsString().Nullable()
				.WithColumn("Name").AsString().NotNullable()
				.WithColumn("Kind").AsInt32().NotNullable()
				.WithColumn("StartUnixNs").AsInt64().NotNullable()
				.WithColumn("EndUnixNs").AsInt64().NotNullable()
				.WithColumn("StatusCode").AsInt32().NotNullable()
				.WithColumn("StatusDescription").AsString().Nullable()
				.WithColumn("AttributesJson").AsString().NotNullable().WithDefaultValue("{}")
				.WithColumn("EventsJson").AsString().NotNullable().WithDefaultValue("[]")
				.WithColumn("LinksJson").AsString().NotNullable().WithDefaultValue("[]");

		// Trace reconstruction (all spans of a trace, in start order) and the time-window scan.
		CreateIndexIfMissing("ix_spans_trace_start", "Spans",
			() => Create.Index("ix_spans_trace_start").OnTable("Spans")
				.OnColumn("TraceId").Ascending()
				.OnColumn("StartUnixNs").Ascending());

		CreateIndexIfMissing("ix_spans_start", "Spans",
			() => Create.Index("ix_spans_start").OnTable("Spans")
				.OnColumn("StartUnixNs").Ascending());

		if (!Schema.Table("MetricPoints").Exists())
			Create.Table("MetricPoints")
				.WithColumn("Id").AsInt64().Nullable().PrimaryKey().Identity()
				.WithColumn("MetricName").AsString().NotNullable()
				.WithColumn("MetricType").AsInt32().NotNullable()
				.WithColumn("Unit").AsString().Nullable()
				.WithColumn("Description").AsString().Nullable()
				.WithColumn("TimeUnixNs").AsInt64().NotNullable()
				.WithColumn("StartUnixNs").AsInt64().Nullable()
				.WithColumn("Flags").AsInt32().Nullable()
				// REAL, not FluentMigrator's `.AsDouble()` (which spells the type "DOUBLE"): the live
				// files declare REAL, and while the two share SQLite's REAL affinity, matching the
				// existing declaration keeps an ADOPTED file byte-identical in schema to a FRESH one.
				.WithColumn("ValueDouble").AsCustom("REAL").Nullable()
				.WithColumn("ValueLong").AsInt64().Nullable()
				.WithColumn("AggregationTemporality").AsInt32().Nullable()
				.WithColumn("IsMonotonic").AsInt32().Nullable()
				.WithColumn("Count").AsInt64().Nullable()
				.WithColumn("Sum").AsCustom("REAL").Nullable()
				.WithColumn("Min").AsCustom("REAL").Nullable()
				.WithColumn("Max").AsCustom("REAL").Nullable()
				.WithColumn("Scale").AsInt32().Nullable()
				.WithColumn("ZeroCount").AsInt64().Nullable()
				.WithColumn("AttributesJson").AsString().NotNullable().WithDefaultValue("{}")
				// Histogram / exponential-histogram / summary payloads: shape depends on MetricType,
				// so they are carried as JSON and left NULL for the types that do not use them.
				.WithColumn("ExplicitBoundsJson").AsString().Nullable()
				.WithColumn("BucketCountsJson").AsString().Nullable()
				.WithColumn("PositiveBucketsJson").AsString().Nullable()
				.WithColumn("NegativeBucketsJson").AsString().Nullable()
				.WithColumn("QuantileValuesJson").AsString().Nullable()
				.WithColumn("ExemplarsJson").AsString().Nullable();

		CreateIndexIfMissing("ix_metricpoints_name_time", "MetricPoints",
			() => Create.Index("ix_metricpoints_name_time").OnTable("MetricPoints")
				.OnColumn("MetricName").Ascending()
				.OnColumn("TimeUnixNs").Ascending());

		CreateIndexIfMissing("ix_metricpoints_time", "MetricPoints",
			() => Create.Index("ix_metricpoints_time").OnTable("MetricPoints")
				.OnColumn("TimeUnixNs").Ascending());
	}

	void CreateIndexIfMissing(string index, string table, Action create)
	{
		if (!Schema.Table(table).Index(index).Exists()) create();
	}

	public override void Down()
	{
		Delete.Table("MetricPoints");
		Delete.Table("Spans");
		Delete.Table("LogEntries");
	}
}
