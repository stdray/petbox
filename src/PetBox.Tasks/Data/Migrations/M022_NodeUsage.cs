using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Usage telemetry for TASK NODES (spec: task-usage-layer-with-declared-role) — the delivery-side
// twin of memory's entry_usage (M007/M008) + delivery_events (M011), landing in the SAME file as
// the nodes themselves so one connection factory, one scope and one migration path cover both.
//
// TWO TABLES, ONE MIGRATION, AND BOTH AXES FROM THE START:
//
//   node_usage            — how often a node was SURFACED (an impression in a search/listing
//                           answer) vs OPENED (tasks_node_get, the mirror of memory_get; a UI
//                           click is deliberately NOT counted). Keyed by (Board, NodeId), NOT by
//                           slug: a node survives a re-key and its history must survive it too.
//
//   node_delivery_events  — one append-only row per delivered node: what the delivery COST in
//                           context (DeliveredChars/BodyChars/RowChars) and how well it FIT
//                           (Rank/ScoreRaw/KRel). `surfaced 40 / opened 0` is either a perfect
//                           index entry or pure noise and the counters alone CANNOT tell them
//                           apart — cost and fit are what separate them, so they ship together
//                           rather than as a follow-up.
//
// DeliberateCount / UsageSource are in THIS migration, not a later one. Memory added its
// equivalent split two months after the counters (M008 after M007) and for those two months the
// signal blended automated context priming with deliberate reads — unusable for the deletion
// decision it existed to support. That mistake is not being repeated here.
//
// DeclaredRole is written INTO each delivery event (the board's declared role at delivery time),
// so the cost/fit numbers cannot be queried without the role that makes them interpretable.
//
// Append-only telemetry: losing rows loses statistics, never state.
[Migration(22, "Per-node usage counters + per-delivery events with declared role")]
public sealed class M022_NodeUsage : Migration
{
	public override void Up()
	{
		// The counter key is the COMPOSITE (Board, NodeId), declared inline: SQLite cannot add a
		// primary key to an existing table, and the recorder's upsert is an
		// ON CONFLICT(Board, NodeId) DO UPDATE, which needs exactly this uniqueness constraint to
		// exist or SQLite refuses the statement outright.
		Create.Table("node_usage")
			.WithColumn("Board").AsString().NotNullable().PrimaryKey()
			.WithColumn("NodeId").AsString().NotNullable().PrimaryKey()
			.WithColumn("SurfacedCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("DeliberateCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("OpenedCount").AsInt64().NotNullable().WithDefaultValue(0)
			.WithColumn("LastHitAt").AsString().Nullable();

		Create.Table("node_delivery_events")
			.WithColumn("Id").AsInt64().NotNullable().PrimaryKey().Identity()
			.WithColumn("Ts").AsString().NotNullable()
			.WithColumn("SessionId").AsString().Nullable()
			.WithColumn("Tool").AsString().NotNullable()
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("NodeId").AsString().NotNullable()
			.WithColumn("Key").AsString().NotNullable()
			.WithColumn("DeclaredRole").AsString(20).NotNullable().WithDefaultValue("corpus")
			.WithColumn("DeliveredChars").AsInt64().NotNullable()
			.WithColumn("BodyChars").AsInt64().NotNullable()
			.WithColumn("RowChars").AsInt64().NotNullable()
			.WithColumn("Rank").AsInt64().NotNullable()
			.WithColumn("ScoreRaw").AsDouble().Nullable()
			.WithColumn("KRel").AsDouble().Nullable()
			.WithColumn("UsageSource").AsString().NotNullable();

		// The two read axes this table exists for: a time window (cost over the last N days) and
		// a per-node rollup (what one node has cost / how well it has fitted).
		Create.Index("ix_node_delivery_events_ts").OnTable("node_delivery_events")
			.OnColumn("Ts").Ascending();
		Create.Index("ix_node_delivery_events_node").OnTable("node_delivery_events")
			.OnColumn("Board").Ascending()
			.OnColumn("NodeId").Ascending();
	}

	// No `IF EXISTS`: Up() created both tables.
	public override void Down()
	{
		Delete.Table("node_delivery_events");
		Delete.Table("node_usage");
	}
}
