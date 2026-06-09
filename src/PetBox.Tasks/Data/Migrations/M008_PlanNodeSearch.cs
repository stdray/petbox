using FluentMigrator;

namespace PetBox.Tasks.Data.Migrations;

// Hybrid search store for the Tasks/boards module, mirroring the Memory tier:
//   - plan_nodes_fts: an FTS5 (unicode61) index over the active, non-terminal nodes
//     (Name/Body/Tags), keyed by the stable NodeId, carrying Board (UNINDEXED) so a
//     search can scope to one board. Tokenised + ranked — find a node by paraphrase/word
//     (incl. Russian) rather than exact substring. Boards had NO search before this.
//   - plan_node_vec: one packed float32 embedding per open node, keyed by NodeId, tagged
//     with the producing model + dim (so the query path fuses only same-(model,dim)
//     candidates) and carrying Board for board-scoped semantic search.
// No back-fill of vectors (embed-on-write fills lazily on the next upsert); the FTS
// mirror is rebuilt wholesale on every upsert from the active non-terminal set. Both
// tables live in the project's shared plan file alongside plan_nodes.
[Migration(8, "Create plan_nodes_fts + plan_node_vec for hybrid board search")]
public sealed class M008_PlanNodeSearch : Migration
{
	public override void Up() => Execute.Sql("""
		CREATE VIRTUAL TABLE IF NOT EXISTS plan_nodes_fts USING fts5(
			NodeId UNINDEXED, Board UNINDEXED, Name, Body, Tags, tokenize='unicode61'
		);
		CREATE TABLE IF NOT EXISTS plan_node_vec (
			NodeId TEXT PRIMARY KEY,
			Board TEXT NOT NULL,
			Model TEXT NOT NULL,
			Dim INTEGER NOT NULL,
			Vec BLOB NOT NULL
		);
		""");

	public override void Down() => Execute.Sql("""
		DROP TABLE IF EXISTS plan_nodes_fts;
		DROP TABLE IF EXISTS plan_node_vec;
		""");
}
