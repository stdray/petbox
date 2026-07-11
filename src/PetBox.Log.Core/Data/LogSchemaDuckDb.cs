using LinqToDB.Data;

namespace PetBox.Log.Core.Data;

// Schema bootstrap for a DuckDb-backed LogDb (LogEntries + Spans + MetricPoints). The DuckDb analog of
// LogSchema: same three tables/columns, but the DDL runs THROUGH the LogDb connection (db.Execute) rather
// than a raw ADO connection, because DuckDB's ":memory:" database is per-connection-object — the single
// LogDb connection that seeds and queries must be the one that creates the tables.
//
// WHY THIS ONE HAS NO MIGRATIONS (and must not grow any). Every PERSISTENT database in PetBox is
// version-controlled by a FluentMigrator set with its own VersionInfo — this store is not persistent.
// There is no DuckDB log store in production: the Web host wires LogDb to SQLite only (LogSchema), and
// the only two callers of this bootstrap are TRANSIENT stores built from scratch on every run — the KQL
// test harness (DuckDB `:memory:`, dies with the connection) and tools/KqlBench (a .db in a temp
// directory the bench deletes when it exits). A file that never outlives the process that made it has no
// schema to evolve and nothing to detect drift against; a migration set here would version nothing.
// If a DuckDB store ever becomes durable, that is when it gets its own migration set — not before.
//
// The `IF NOT EXISTS` this bootstrap used to carry is gone with the rest of them: both callers run it
// exactly once, against a brand-new empty database. All the tolerance could do is quietly accept a
// second Ensure on a store that already has tables — i.e. hide the very bug it looks like it prevents.
//
// Type map vs LogSchema (SQLite): `INTEGER PK AUTOINCREMENT` → `BIGINT PRIMARY KEY` (NO identity — the KQL
// test harness seeds explicit Ids via BulkCopy(KeepIdentity)); `TEXT`→`VARCHAR`; `REAL`→`DOUBLE`; the
// integer columns match the record CLR types exactly (long→BIGINT, int→INTEGER, bool?→BOOLEAN) so the
// DuckDB.NET reader never surprises linq2db's column materialization. Indexes are skipped (an in-memory
// smoke store gets no benefit).
public static class LogSchemaDuckDb
{
	public static void Ensure(LogDb db)
	{
		db.Execute("""
			CREATE TABLE LogEntries (
				Id BIGINT PRIMARY KEY,
				ServiceKey VARCHAR NOT NULL,
				TimestampMs BIGINT NOT NULL,
				Level INTEGER NOT NULL,
				Message VARCHAR NOT NULL,
				MessageTemplate VARCHAR NOT NULL,
				Exception VARCHAR,
				PropertiesJson VARCHAR NOT NULL DEFAULT '{}',
				TemplateHash BIGINT NOT NULL DEFAULT 0
			);

			CREATE TABLE Spans (
				SpanId            VARCHAR PRIMARY KEY,
				TraceId           VARCHAR NOT NULL,
				ParentSpanId      VARCHAR,
				Name              VARCHAR NOT NULL,
				Kind              INTEGER NOT NULL,
				StartUnixNs       BIGINT  NOT NULL,
				EndUnixNs         BIGINT  NOT NULL,
				StatusCode        INTEGER NOT NULL,
				StatusDescription VARCHAR,
				AttributesJson    VARCHAR NOT NULL DEFAULT '{}',
				EventsJson        VARCHAR NOT NULL DEFAULT '[]',
				LinksJson         VARCHAR NOT NULL DEFAULT '[]'
			);

			CREATE TABLE MetricPoints (
				Id                     BIGINT PRIMARY KEY,
				MetricName             VARCHAR NOT NULL,
				MetricType             INTEGER NOT NULL,
				Unit                   VARCHAR,
				Description            VARCHAR,
				TimeUnixNs             BIGINT  NOT NULL,
				StartUnixNs            BIGINT,
				Flags                  INTEGER,
				ValueDouble            DOUBLE,
				ValueLong              BIGINT,
				AggregationTemporality INTEGER,
				IsMonotonic            BOOLEAN,
				Count                  BIGINT,
				Sum                    DOUBLE,
				Min                    DOUBLE,
				Max                    DOUBLE,
				Scale                  INTEGER,
				ZeroCount              BIGINT,
				AttributesJson         VARCHAR NOT NULL DEFAULT '{}',
				ExplicitBoundsJson     VARCHAR,
				BucketCountsJson       VARCHAR,
				PositiveBucketsJson    VARCHAR,
				NegativeBucketsJson    VARCHAR,
				QuantileValuesJson     VARCHAR,
				ExemplarsJson          VARCHAR
			);
			""");
	}
}
