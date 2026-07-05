using Microsoft.Data.Sqlite;

namespace PetBox.Log.Core.Data;

// Lazy schema bootstrap for a per-log SQLite file (LogEntries + Spans + MetricPoints). Passed to
// ScopedDbFactory<LogDb> as the ensure-schema delegate; runs once per file on
// first open. Idempotent (CREATE TABLE/INDEX IF NOT EXISTS).
public static class LogSchema
{
	public static void Ensure(string connectionString)
	{
		using var raw = new SqliteConnection(connectionString);
		raw.Open();
		using var cmd = raw.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS LogEntries (
				Id INTEGER PRIMARY KEY AUTOINCREMENT,
				ServiceKey TEXT NOT NULL,
				TimestampMs INTEGER NOT NULL,
				Level INTEGER NOT NULL,
				Message TEXT NOT NULL,
				MessageTemplate TEXT NOT NULL,
				Exception TEXT,
				PropertiesJson TEXT NOT NULL DEFAULT '{}',
				TemplateHash INTEGER NOT NULL DEFAULT 0
			);
			CREATE INDEX IF NOT EXISTS IX_LogEntries_ServiceKey_TimestampMs ON LogEntries (ServiceKey, TimestampMs DESC);
			CREATE INDEX IF NOT EXISTS IX_LogEntries_TimestampMs ON LogEntries (TimestampMs DESC);
			CREATE INDEX IF NOT EXISTS IX_LogEntries_Level ON LogEntries (Level);

			CREATE TABLE IF NOT EXISTS Spans (
				SpanId            TEXT    PRIMARY KEY,
				TraceId           TEXT    NOT NULL,
				ParentSpanId      TEXT,
				Name              TEXT    NOT NULL,
				Kind              INTEGER NOT NULL,
				StartUnixNs       INTEGER NOT NULL,
				EndUnixNs         INTEGER NOT NULL,
				StatusCode        INTEGER NOT NULL,
				StatusDescription TEXT,
				AttributesJson    TEXT    NOT NULL DEFAULT '{}',
				EventsJson        TEXT    NOT NULL DEFAULT '[]',
				LinksJson         TEXT    NOT NULL DEFAULT '[]'
			);
			CREATE INDEX IF NOT EXISTS ix_spans_trace_start ON Spans(TraceId, StartUnixNs);
			CREATE INDEX IF NOT EXISTS ix_spans_start ON Spans(StartUnixNs);

			CREATE TABLE IF NOT EXISTS MetricPoints (
				Id                     INTEGER PRIMARY KEY AUTOINCREMENT,
				MetricName             TEXT    NOT NULL,
				MetricType             INTEGER NOT NULL,
				Unit                   TEXT,
				Description            TEXT,
				TimeUnixNs             INTEGER NOT NULL,
				StartUnixNs            INTEGER,
				Flags                  INTEGER,
				ValueDouble            REAL,
				ValueLong              INTEGER,
				AggregationTemporality INTEGER,
				IsMonotonic            INTEGER,
				Count                  INTEGER,
				Sum                    REAL,
				Min                    REAL,
				Max                    REAL,
				Scale                  INTEGER,
				ZeroCount              INTEGER,
				AttributesJson         TEXT    NOT NULL DEFAULT '{}',
				ExplicitBoundsJson     TEXT,
				BucketCountsJson       TEXT,
				PositiveBucketsJson    TEXT,
				NegativeBucketsJson    TEXT,
				QuantileValuesJson     TEXT,
				ExemplarsJson          TEXT
			);
			CREATE INDEX IF NOT EXISTS ix_metricpoints_name_time ON MetricPoints(MetricName, TimeUnixNs);
			CREATE INDEX IF NOT EXISTS ix_metricpoints_time ON MetricPoints(TimeUnixNs);
			""";
		cmd.ExecuteNonQuery();
	}
}
