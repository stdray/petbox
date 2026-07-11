using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Relations move OUT of the Core DB and INTO the project's own tasks file, next to the
// nodes they point at (relations-in-project-db). Two tables:
//
//   plan_node_ids — the node-identity registry: one row per stable NodeId that has at
//                   least one revision in plan_nodes.
//   relations     — the typed edges, with a REAL foreign key on both endpoints.
//
// WHY THE REGISTRY EXISTS (the crux). plan_nodes is temporal (SCD-2): one node identity
// has MANY rows (one per revision), so NodeId is not unique there and SQLite cannot use
// it as a FK parent — a FK parent key must be a PRIMARY KEY or have a plain UNIQUE index,
// and a partial index (`... WHERE ActiveTo IS NULL`) is explicitly NOT eligible. So the
// edges get a parent table that IS one-row-per-identity, and triggers derive it from
// plan_nodes so it can never drift from the table it indexes:
//
//   insert a revision            -> INSERT OR IGNORE the NodeId into the registry
//   delete the LAST revision     -> delete the NodeId from the registry
//                                   -> FK ON DELETE CASCADE takes the node's edges with it
//
// That second rule is the dangling-edge fix. A node's revisions are hard-deleted only when
// its whole BOARD is deleted (TaskBoardStore.DeleteAsync); a single node "delete" is a soft
// close (ActiveTo set, rows kept). Board deletes are exactly how the pre-move edges went
// dangling — the nodes vanished from the tasks file while the edges sat untouched in
// petbox.db. Now they cascade.
//
// WHAT THE DB ENFORCES: an edge endpoint must be a NodeId that EXISTS (has revisions) in
// this project's file. An INSERT naming an unknown NodeId is rejected by the FK; edges of a
// node whose revisions are all deleted are cascaded away. WHAT IT DOES NOT: an edge to a
// SOFT-deleted (closed, ActiveTo set) node is still structurally legal — the revisions are
// still there, that is what history means. Closing those edges stays a service-layer job
// (TaskTransitionEffects), as it was.
//
// FK enforcement needs PRAGMA foreign_keys=ON per connection: TasksDb.CreateOptions appends
// `Foreign Keys=True` to every connection string, so the app path (and every store) has it.
//
// STYLE (schema-only-via-migrations): typed FluentMigrator API, no `IF NOT EXISTS` — a
// migration runs exactly once per VersionInfo, so IF NOT EXISTS would only swallow schema
// drift. The three things the typed API cannot express go through the NAMED, guarded SqliteDdl
// helpers rather than an anonymous Execute.Sql: the triggers (SqliteDdl.Trigger), the PARTIAL
// (filtered) indexes (SqliteDdl.PartialIndex — .Filter() is SqlServer-only and would SILENTLY
// drop the WHERE here, turning the active-edge uniqueness into a TOTAL unique index that forbids
// ever re-creating a closed edge), and the INSERT..SELECT seed (SqliteDdl.Raw). Forward-only.
[Migration(14, "relations + plan_node_ids registry (edges move into the project file, with a real FK)")]
public sealed class M014_Relations : SqliteMigration
{
	public override void Up()
	{
		Create.Table("plan_node_ids")
			.WithColumn("NodeId").AsString().NotNullable().PrimaryKey();

		// The FK must be INLINE in CREATE TABLE: SQLite has no ALTER TABLE ADD CONSTRAINT, so a
		// separate Create.ForeignKey() would be silently dropped by the SQLite generator. The
		// column-level .ForeignKey(...) below is emitted inside the CREATE TABLE statement.
		Create.Table("relations")
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("Kind").AsString().NotNullable()
			.WithColumn("FromNodeId").AsString().NotNullable()
				.ForeignKey("fk_relations_from", "plan_node_ids", "NodeId").OnDelete(System.Data.Rule.Cascade)
			.WithColumn("ToNodeId").AsString().NotNullable()
				.ForeignKey("fk_relations_to", "plan_node_ids", "NodeId").OnDelete(System.Data.Rule.Cascade)
			.WithColumn("CreatedAt").AsString().NotNullable()
			.WithColumn("ClosedAt").AsString().Nullable();

		SqliteDdl.Raw(
			"seed the identity registry from the nodes already in this file (empty NodeId = a pre-M003 " +
			"row, skipped). INSERT..SELECT is DML — Insert.IntoTable takes literal rows only",
			"INSERT INTO plan_node_ids (NodeId) SELECT DISTINCT NodeId FROM plan_nodes WHERE NodeId <> '';");

		// Registry follows plan_nodes mechanically — it is never written by application code.
		SqliteDdl.Trigger(
			name: "trg_plan_nodes_register_id",
			table: "plan_nodes",
			when: "AFTER INSERT",
			condition: "NEW.NodeId <> ''",
			body: "INSERT OR IGNORE INTO plan_node_ids (NodeId) VALUES (NEW.NodeId);");

		// A NodeId is meant to be immutable, but the flat/part_of back-fill rewrites rows in
		// place — keep the registry correct even if some path re-stamps the column.
		SqliteDdl.Trigger(
			name: "trg_plan_nodes_register_id_upd",
			table: "plan_nodes",
			when: "AFTER UPDATE OF NodeId",
			condition: "NEW.NodeId <> ''",
			body: "INSERT OR IGNORE INTO plan_node_ids (NodeId) VALUES (NEW.NodeId);");

		// Last revision of an identity gone (board delete) => the identity is gone => its edges
		// cascade. FK actions fire for statements inside a trigger body, independent of
		// PRAGMA recursive_triggers.
		SqliteDdl.Trigger(
			name: "trg_plan_nodes_unregister_id",
			table: "plan_nodes",
			when: "AFTER DELETE",
			condition: "OLD.NodeId <> '' AND NOT EXISTS (SELECT 1 FROM plan_nodes WHERE NodeId = OLD.NodeId)",
			body: "DELETE FROM plan_node_ids WHERE NodeId = OLD.NodeId;");

		// PARTIAL indexes — no typed-API equivalent.
		// At most one ACTIVE edge per (kind, from, to) — the store's idempotent-create, enforced.
		SqliteDdl.PartialIndex(
			name: "ux_relations_active",
			table: "relations",
			columns: ["Kind", "FromNodeId", "ToNodeId"],
			where: "ClosedAt IS NULL",
			unique: true);

		// Endpoint lookups (ListAsync from/to/both) and the kind sweep (ListByKindAsync).
		SqliteDdl.PartialIndex("ix_relations_from", "relations", ["FromNodeId"], "ClosedAt IS NULL");
		SqliteDdl.PartialIndex("ix_relations_to", "relations", ["ToNodeId"], "ClosedAt IS NULL");
		SqliteDdl.PartialIndex("ix_relations_kind", "relations", ["Kind"], "ClosedAt IS NULL");
	}

	public override void Down() { } // forward-only
}
