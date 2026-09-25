using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// node-snooze-until + snooze-wakes-without-a-human (work node-snooze-and-wake-job, idea
// recurring-run-scheduler): the node's alarm as four COLUMNS of plan_nodes, not a status. A status
// would lose the work phase (a snoozed node keeps whatever status it had — the spec forbids the wake
// from changing it) and would live in the methodology document, whose edit needs a live-node
// migrator. They are PAYLOAD fields (TaskNode.SamePayload), so snoozing and waking mint revisions.
//
//   SnoozeUntil  — the wake date; NULL = not snoozed. Stored as TEXT like every other plan_nodes
//                  timestamp (M001 Created/Updated).
//   SnoozeReason — the free-text condition for whoever wakes it ("" = none).
//   SnoozeWakeTo — the addressee, agent|owner ("" = never snoozed).
//   WokeAt       — when the daily job woke it; NULL = not woken (or the mark was cleared).
//
// Backfill: NULL/"" for every existing revision — no node has ever been snoozed, so that is the
// true historic value. Typed ALTER TABLE ADD COLUMN (the M021 precedent). Forward-only.
[Migration(27, "plan_nodes.SnoozeUntil/SnoozeReason/SnoozeWakeTo/WokeAt (node-snooze-until)")]
public sealed class M027_NodeSnooze : Migration
{
	public override void Up()
	{
		Alter.Table("plan_nodes").AddColumn("SnoozeUntil").AsString().Nullable();
		Alter.Table("plan_nodes").AddColumn("SnoozeReason").AsString().NotNullable().WithDefaultValue("");
		Alter.Table("plan_nodes").AddColumn("SnoozeWakeTo").AsString().NotNullable().WithDefaultValue("");
		Alter.Table("plan_nodes").AddColumn("WokeAt").AsString().Nullable();
	}

	public override void Down() { } // forward-only
}
