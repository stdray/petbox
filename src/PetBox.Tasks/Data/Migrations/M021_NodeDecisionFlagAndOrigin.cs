using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Two spec leaves, one schema move (owner-decision-pending-flag + node-origin-provenance).
//
// 1. plan_nodes.DecisionPending — the owner-decision-pending flag as a COLUMN of the node,
//    ORTHOGONAL to Status, not a status of its own. A status would lose the work phase (a node
//    can be InProgress AND waiting on the owner at the same time) and would live in the
//    methodology document, whose edit needs a live-node migrator of the WorkDeferredStatusMigrator
//    class. It is a payload field (TaskNode.SamePayload), so flipping it mints a node revision —
//    that is deliberate: the flip IS the event the owner digest reads off tasks_delta.
//    Backfilled to 0 for every existing revision: no node has been marked pending yet, so "not
//    waiting" is the true historic value, not a guess.
//
// 2. plan_nodes.OriginSessionId — the WRITE-ONCE half of node provenance: the session the node
//    was CREATED in. Empty for every pre-existing node and for every node created by a caller
//    that passes no sessionId; that emptiness is a permanent property of the node, never
//    backfilled later (a node was not born in whichever session happened to edit it next).
//
// 3. plan_node_sessions — the ACCUMULATING half: the UNION of sessions that have touched the
//    node. An association, not a column, so growing it never mints a node revision (the
//    `Commits` precedent). PK (NodeId, SessionId) makes the union an INVARIANT of the schema
//    rather than a promise of the writer: a repeat touch by the same session cannot insert a
//    second row. See TaskNodeOriginSession.
//
// The two columns go on with typed ALTER TABLE ADD COLUMN (the M002 precedent — SQLite supports
// it and it is expressible in the typed API, so no table rebuild and no raw SQL is warranted
// here). Only the partial index has no typed form and goes through the named, guarded
// SqliteDdl helper, exactly as M011 did for plan_node_commits. Forward-only.
//
// Numbered 21 — the next free number after M020 in this tier's used-migration-numbers registry
// (a deleted number is BURNED, never reused; M015 is such a burned number). 22 is claimed by a
// concurrently-developed card in this same module.
[Migration(21, "plan_nodes.DecisionPending + plan_nodes.OriginSessionId + plan_node_sessions (owner-decision-pending-flag, node-origin-provenance)")]
public sealed class M021_NodeDecisionFlagAndOrigin : Migration
{
	public override void Up()
	{
		// Defaults are what backfill EXISTING revisions: false = "not waiting on the owner",
		// "" = "no origin session recorded". Both are the true historic value, not a placeholder.
		Alter.Table("plan_nodes")
			.AddColumn("DecisionPending").AsBoolean().NotNullable().WithDefaultValue(false);
		Alter.Table("plan_nodes")
			.AddColumn("OriginSessionId").AsString().NotNullable().WithDefaultValue("");

		Create.Table("plan_node_sessions")
			.WithColumn("NodeId").AsString().NotNullable().PrimaryKey()
			.WithColumn("SessionId").AsString().NotNullable().PrimaryKey()
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("FirstSeen").AsString().NotNullable();

		// The board-scoped read (every node's provenance on one board — the shape GetAsync needs).
		// The per-NODE read needs no index of its own: SQLite's automatic index for the composite
		// PRIMARY KEY leads with NodeId, so one node's rows are already an index seek. No
		// SessionId-leading read exists yet, so none is created — an index nothing queries is
		// pure write cost.
		Create.Index("ix_plan_node_sessions_board").OnTable("plan_node_sessions")
			.OnColumn("Board").Ascending();
	}

	public override void Down() { } // forward-only
}
