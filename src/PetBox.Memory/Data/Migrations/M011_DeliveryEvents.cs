using FluentMigrator;

namespace PetBox.Memory.Data.Migrations;

// Per-DELIVERY events (spec: memory-usage-observability + usage-cost-and-fit-separate).
// entry_usage answers "how often was this entry reached" — a fast counter cache, and it stays.
// What it CANNOT answer is the pair the cost/fit question needs: how much context a delivery
// actually SPENT (chars on the wire) and how well the entry FIT the ask (relevance) — two
// separate axes that must not be collapsed into one scalar. So we keep the raw components,
// one row per (entry, delivery):
//
//   DeliveredChars — the body chars actually sent in THIS row (bodyLen contract already applied)
//   BodyChars      — the entry's full body length (DeliveredChars/BodyChars = how much was cut)
//   RowChars       — the row's whole serialized wire cost (the honest context price, incl. envelope)
//   Rank           — 1-based position in the delivered answer (MMR reorders rows without touching score)
//   ScoreRaw       — the fused RRF score BEFORE recency decay (null in a listing / get: no relevance leg)
//   KRel           — ScoreRaw / ScoreRaw of the request's top-1 hit → a within-request [0,1] fit
//                    normalization. Raw RRF is NOT in [0,1] (its ceiling is ~1/K ≈ 0.033, see
//                    HybridMerge), so it is meaningless across requests until normalized; and the
//                    normalization takes the PRE-decay score, or freshness would count twice (decay
//                    already reordered the answer). memory_get is by definition a perfect fit: KRel = 1.
//   UsageSource    — deliberate | machine (same honest split entry_usage.DeliberateCount records)
//   SessionId      — the MCP session the delivery went to (null on a stateless transport)
//   Tool           — search | get | listing
//   Scope          — project | workspace: how the container was reached (the row lives in the
//                    container's own file, so the project is implicit)
//
// Append-only telemetry: losing rows loses statistics, never state.
[Migration(11, "Per-delivery memory events (delivery_events)")]
public sealed class M011_DeliveryEvents : Migration
{
	public override void Up()
	{
		Create.Table("delivery_events")
			.WithColumn("Id").AsInt64().NotNullable().PrimaryKey().Identity()
			.WithColumn("Ts").AsString().NotNullable()
			.WithColumn("SessionId").AsString().Nullable()
			.WithColumn("Tool").AsString().NotNullable()
			.WithColumn("Scope").AsString().NotNullable()
			.WithColumn("Store").AsString().NotNullable()
			.WithColumn("Key").AsString().NotNullable()
			.WithColumn("DeliveredChars").AsInt64().NotNullable()
			.WithColumn("BodyChars").AsInt64().NotNullable()
			.WithColumn("RowChars").AsInt64().NotNullable()
			.WithColumn("Rank").AsInt64().NotNullable()
			.WithColumn("ScoreRaw").AsDouble().Nullable()
			.WithColumn("KRel").AsDouble().Nullable()
			.WithColumn("UsageSource").AsString().NotNullable();

		// The two read axes this table exists for: a time window (cost over the last N days) and
		// a per-entry rollup (what one entry has cost / how well it has fitted).
		Create.Index("ix_delivery_events_ts").OnTable("delivery_events")
			.OnColumn("Ts").Ascending();
		Create.Index("ix_delivery_events_entry").OnTable("delivery_events")
			.OnColumn("Store").Ascending()
			.OnColumn("Key").Ascending();
	}

	public override void Down() => Delete.Table("delivery_events");
}
