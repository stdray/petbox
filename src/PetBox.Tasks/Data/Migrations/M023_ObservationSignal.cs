using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Recurrence signal for kind `observation` nodes (work observation-kind-and-dedup, spec
// observation-recurrence-is-ranked) — ONE row per observation node, growing on every dedup
// hit instead of a new node being born. Deliberately its OWN table rather than columns on
// plan_nodes: plan_nodes is the SCD-2 revision table shared by every kind on every board,
// and this signal is not a revisioned field (it never gets its own history row) — it is a
// plain counter keyed by NodeId, closer in shape to node_usage (M022) than to a node
// attribute. No FK to plan_node_ids (unlike M014_Relations): the `observations` board is
// system-protected and can never be deleted (SystemBoards.IsSystem /
// TasksService.DeleteBoardAsync), so the dangling-edge problem that FK exists to solve for
// `relations` cannot arise here — a node soft-close (the only per-node delete) keeps its
// plan_nodes row, so the signal row it points at stays meaningful too.
//
//   RecurrenceCount     — total sightings of this observation (1 at creation, +1 per dedup
//                         hit — AutocaptureDedup.FindDuplicateKeyAsync matching this node's
//                         text against a fresh candidate).
//   LastSeenAt          — the most recent sighting (creation or dedup hit).
//   RecurredAfterFixAt  — set (and overwritten on each later occurrence) the moment a dedup
//                         hit lands on a node whose status is `fixed` — the exact signal a
//                         regression detector (a neighboring, not-yet-built card) needs:
//                         "we called this done and it came back". Null = never recurred
//                         after a fix, including the common case of never having been fixed.
//   FixedByNodeId       — reserved for the promote/fix tooling (a neighboring card) to name
//                         what closed this observation; left null and unwritten by this card.
[Migration(23, "observation_signal — recurrence counter per observation node")]
public sealed class M023_ObservationSignal : Migration
{
	public override void Up()
	{
		Create.Table("observation_signal")
			.WithColumn("NodeId").AsString().NotNullable().PrimaryKey()
			.WithColumn("RecurrenceCount").AsInt64().NotNullable().WithDefaultValue(1)
			.WithColumn("LastSeenAt").AsString().NotNullable()
			.WithColumn("RecurredAfterFixAt").AsString().Nullable()
			.WithColumn("FixedByNodeId").AsString().Nullable();
	}

	public override void Down() => Delete.Table("observation_signal");
}
