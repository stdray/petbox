using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;

namespace PetBox.Tasks.Data;

// linq2db context over a project's task file (data/tasks/{project}.db) — all of the
// project's boards share it, partitioned by PlanNode.Board.
public sealed class TasksDb : DataConnection
{
	public TasksDb(DataOptions<TasksDb> options) : base(options.Options) { DisposeConnection = true; }

	public ITable<PlanNode> PlanNodes => this.GetTable<PlanNode>();
	public ITable<NodeTag> NodeTags => this.GetTable<NodeTag>();
	public ITable<TagVocab> TagVocab => this.GetTable<TagVocab>();
	public ITable<PlanNodeCommit> PlanNodeCommits => this.GetTable<PlanNodeCommit>();
	// Lexical (search_fts) + vector (search_vec) live behind PetBox.Core.Search indexes, which
	// own their own row mappings — no table props here. See the TasksService search seam.

	// Foreign Keys=True turns on per-connection FK enforcement (SQLite defaults it OFF),
	// so node_tag.Tag -> tag_vocab.Tag is actually enforced. plan_nodes has no FK.
	public static DataOptions<TasksDb> CreateOptions(string connectionString)
	{
		if (!connectionString.Contains("Foreign Keys", StringComparison.OrdinalIgnoreCase))
			connectionString = connectionString.TrimEnd(';') + ";Foreign Keys=True";
		return new(new DataOptions().UseSQLite(connectionString));
	}
}
