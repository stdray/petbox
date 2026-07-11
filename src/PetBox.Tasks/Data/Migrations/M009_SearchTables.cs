using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Retrofit board search behind the PetBox.Core.Search contract (mirrors memory M006). Replaces
// plan_nodes_fts / plan_node_vec (keyed by NodeId) with the contract's entity-addressed tables:
// search_fts (Class-A lexical floor, written INSIDE the entity tx) + search_vec (Class-B vectors,
// dim 1024, materialized by the async-vectorization worker) + the worker's durable cursor/
// dead-letter state. Entity address: Scope=projectKey, Type=Board, Id=node slug (the temporal Key)
// — so the temporal log's slugs map straight through and the worker's per-board cursor uses
// IndexName=Board. The file is shared by all of a project's boards. Lexical content backfills on
// first search; vectors re-embed from cursor 0.
//
// Everything here is typed DDL except the FTS5 virtual table, which has no typed form and is
// SQLite-specific (SqliteDdl.Fts5Table — guarded, so it cannot silently no-op on another engine).
//
// The DROPs carry no `IF EXISTS`: plan_nodes_fts and plan_node_vec are both created by M008, which
// VersionInfo guarantees ran before this one. If they are somehow absent, that is schema drift and
// this migration SHOULD fail loudly instead of shrugging.
[Migration(9, "Replace plan_nodes_fts/plan_node_vec with contract search tables")]
public sealed class M009_SearchTables : SqliteMigration
{
	public override void Up()
	{
		SqliteDdl.Fts5Table(
			name: "search_fts",
			columns: ["Scope", "Type", "Id", "Text", "Tags"],
			unindexed: ["Scope", "Type", "Id"], // the entity address: stored, not tokenised
			tokenize: "unicode61");

		Create.Table("search_vec")
			.WithColumn("Scope").AsString().NotNullable().PrimaryKey()
			.WithColumn("Type").AsString().NotNullable().PrimaryKey()
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("Model").AsString().NotNullable()
			.WithColumn("Dim").AsInt32().NotNullable()
			.WithColumn("Vec").AsBinary().NotNullable();

		Create.Table("search_cursor")
			// Nullable PK, deliberately: the original DDL said `IndexName TEXT PRIMARY KEY`, and SQLite
			// does not imply NOT NULL on a non-INTEGER PK (FluentMigrator would default it to NOT NULL).
			// Every tasks file on disk has this column nullable; tightening it is a separate, deliberate
			// migration, not a side effect of a refactor.
			.WithColumn("IndexName").AsString().Nullable().PrimaryKey()
			.WithColumn("Version").AsInt64().NotNullable();

		Create.Table("search_deadletter")
			.WithColumn("IndexName").AsString().NotNullable().PrimaryKey()
			.WithColumn("Type").AsString().NotNullable().PrimaryKey()
			.WithColumn("Id").AsString().NotNullable().PrimaryKey()
			.WithColumn("Attempts").AsInt32().NotNullable()
			.WithColumn("Dead").AsBoolean().NotNullable();

		Delete.Table("plan_nodes_fts"); // created by M008
		Delete.Table("plan_node_vec");  // created by M008
	}

	// Symmetric inverse of Up(): the four tables it created are dropped, the two it dropped are
	// recreated in their M008 shape. Again no `IF EXISTS` — Down() runs only after Up().
	public override void Down()
	{
		Delete.Table("search_fts");
		Delete.Table("search_vec");
		Delete.Table("search_cursor");
		Delete.Table("search_deadletter");

		SqliteDdl.Fts5Table(
			name: "plan_nodes_fts",
			columns: ["NodeId", "Board", "Name", "Body", "Tags"],
			unindexed: ["NodeId", "Board"],
			tokenize: "unicode61");

		Create.Table("plan_node_vec")
			.WithColumn("NodeId").AsString().Nullable().PrimaryKey() // nullable PK: see M008
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("Model").AsString().NotNullable()
			.WithColumn("Dim").AsInt32().NotNullable()
			.WithColumn("Vec").AsBinary().NotNullable();
	}
}
