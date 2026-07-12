using FluentMigrator;

namespace PetBox.Log.Core.Data.Migrations;

// The NATURAL KEY of a metric point, so an OTLP retry cannot double-count.
//
// Spans already had one (SpanId is the PK), metric points had none: MetricPoints.Id is an
// autoincrement surrogate, so a re-sent batch — which a stock OTLP exporter emits on any
// timeout/5xx — landed a second, identical set of points and silently inflated every counter and
// histogram built over them.
//
// THE KEY IS (MetricName, MetricType, TimeUnixNs, AttributesJson). That is the OTLP data model's own
// notion of point identity: within a metric stream a point is identified by its attribute set and its
// timestamp. Everything else on the row is DATA, not identity —
//   - the value (ValueDouble/ValueLong/Count/Sum/…) is deliberately OUT: if it were in the key, a
//     retried point whose value the exporter recomputed (a cumulative counter that ticked between the
//     send and the retry) would hash differently and double-count. That is the bug, not a fix for it.
//   - StartUnixNs is OUT too, and that is what keeps the index NULL-free: it is nullable, and SQLite
//     treats NULLs in a UNIQUE index as distinct — a gauge without a start time would never conflict
//     with itself. All four key columns are NOT NULL, so the constraint actually bites.
// AttributesJson is compared as text: an exporter's retry re-serializes the same in-memory point, so
// the bytes are identical.
//
// The DELETE first: live files already carry duplicates from the bug (that is the bug), and a UNIQUE
// index cannot be created over them. Collapse each identity to its earliest row (MIN(Id)) — the rows
// dropped are by definition indistinguishable copies of a kept one.
[Migration(2, "Metric-point natural key: dedupe, then a unique identity index")]
public sealed class M002_MetricPointIdentity : Migration
{
	public override void Up()
	{
		Execute.Sql("""
			DELETE FROM MetricPoints
			WHERE Id NOT IN (
				SELECT MIN(Id) FROM MetricPoints
				GROUP BY MetricName, MetricType, TimeUnixNs, AttributesJson
			);
			""");

		Create.Index("ux_metricpoints_identity").OnTable("MetricPoints")
			.OnColumn("MetricName").Ascending()
			.OnColumn("MetricType").Ascending()
			.OnColumn("TimeUnixNs").Ascending()
			.OnColumn("AttributesJson").Ascending()
			.WithOptions().Unique();
	}

	public override void Down() =>
		Delete.Index("ux_metricpoints_identity").OnTable("MetricPoints");
}
