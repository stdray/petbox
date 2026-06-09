using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;

namespace PetBox.Tasks.Data;

// linq2db context over a project's task file (data/tasks/{project}.db) — all of the
// project's boards share it, partitioned by PlanNode.Board.
public sealed class TasksDb : DataConnection
{
	public TasksDb(DataOptions<TasksDb> options) : base(options.Options) { }

	public ITable<PlanNode> PlanNodes => this.GetTable<PlanNode>();
	public ITable<NodeTag> NodeTags => this.GetTable<NodeTag>();
	public ITable<TagVocab> TagVocab => this.GetTable<TagVocab>();
	public ITable<PlanNodeFts> PlanNodesFts => this.GetTable<PlanNodeFts>();
	public ITable<PlanNodeVec> PlanNodeVec => this.GetTable<PlanNodeVec>();

	// Foreign Keys=True turns on per-connection FK enforcement (SQLite defaults it OFF),
	// so node_tag.Tag -> tag_vocab.Tag is actually enforced. plan_nodes has no FK.
	public static DataOptions<TasksDb> CreateOptions(string connectionString)
	{
		if (!connectionString.Contains("Foreign Keys", StringComparison.OrdinalIgnoreCase))
			connectionString = connectionString.TrimEnd(';') + ";Foreign Keys=True";
		return new(new DataOptions().UseSQLite(connectionString));
	}
}
