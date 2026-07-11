using FluentMigrator;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data.Migrations;

// Hybrid search store for the Tasks/boards module, mirroring the Memory tier:
//   - plan_nodes_fts: an FTS5 (unicode61) index over the active, non-terminal nodes (Name/Body/
//     Tags), keyed by the stable NodeId, carrying Board (UNINDEXED) so a search can scope to one
//     board. Tokenised + ranked — find a node by paraphrase/word (incl. Russian) rather than exact
//     substring. Boards had NO search before this.
//   - plan_node_vec: one packed float32 embedding per open node, keyed by NodeId, tagged with the
//     producing model + dim (so the query path fuses only same-(model,dim) candidates) and carrying
//     Board for board-scoped semantic search.
// No back-fill of vectors (embed-on-write fills lazily on the next upsert); the FTS mirror is
// rebuilt wholesale on every upsert from the active non-terminal set. Both tables live in the
// project's shared plan file alongside plan_nodes. M009 replaces both with the contract's
// entity-addressed search tables.
//
// The FTS5 virtual table is the one thing here with no typed form (and it is SQLite-specific), so
// it goes through the named, guarded SqliteDdl.Fts5Table; plan_node_vec is ordinary typed DDL.
[Migration(8, "Create plan_nodes_fts + plan_node_vec for hybrid board search")]
public sealed class M008_PlanNodeSearch : SqliteMigration
{
	public override void Up()
	{
		SqliteDdl.Fts5Table(
			name: "plan_nodes_fts",
			columns: ["NodeId", "Board", "Name", "Body", "Tags"],
			unindexed: ["NodeId", "Board"], // the address: stored, not tokenised
			tokenize: "unicode61");

		Create.Table("plan_node_vec")
			// `.Nullable()` on a PRIMARY KEY column looks wrong and is deliberate: the original DDL
			// said `NodeId TEXT PRIMARY KEY`, and SQLite does NOT imply NOT NULL on a non-INTEGER
			// primary key, where FluentMigrator would default a PK column to NOT NULL. Reproducing the
			// shape that was on disk, not tightening it as a side effect of a refactor.
			.WithColumn("NodeId").AsString().Nullable().PrimaryKey()
			.WithColumn("Board").AsString().NotNullable()
			.WithColumn("Model").AsString().NotNullable()
			.WithColumn("Dim").AsInt32().NotNullable()
			.WithColumn("Vec").AsBinary().NotNullable();
	}

	// No `IF EXISTS`: Up() created both, so Down() finds both.
	public override void Down()
	{
		Delete.Table("plan_nodes_fts");
		Delete.Table("plan_node_vec");
	}
}
