using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;

namespace PetBox.Tasks.Data;

// linq2db context over a project's task file (data/tasks/{project}.db) — all of the
// project's boards share it, partitioned by TaskNode.Board.
public sealed class TasksDb : DataConnection
{
	public TasksDb(DataOptions<TasksDb> options) : base(options.Options) { }

	public ITable<TaskNode> TaskNodes => this.GetTable<TaskNode>();
	public ITable<NodeTag> NodeTags => this.GetTable<NodeTag>();
	public ITable<TagVocab> TagVocab => this.GetTable<TagVocab>();
	public ITable<TaskNodeCommit> TaskNodeCommits => this.GetTable<TaskNodeCommit>();
	public ITable<TaskNodeOriginSession> TaskNodeOriginSessions => this.GetTable<TaskNodeOriginSession>();
	// Usage telemetry for node DELIVERY (M022) — deliberately in the SAME file as the nodes, the
	// way memory keeps entry + usage + delivery together: one connection factory, one scope, one
	// migration path. Never load-bearing; losing rows loses statistics, not state.
	public ITable<NodeUsage> NodeUsage => this.GetTable<NodeUsage>();
	public ITable<NodeDeliveryEvent> NodeDeliveries => this.GetTable<NodeDeliveryEvent>();
	// Lexical (search_fts) + vector (search_vec) live behind PetBox.Core.Search indexes, which
	// own their own row mappings — no table props here. See the TasksService search seam.

	// Foreign Keys=True turns on per-connection FK enforcement (SQLite defaults it OFF),
	// so node_tag.Tag -> tag_vocab.Tag is actually enforced. plan_nodes has no FK.
	public static DataOptions<TasksDb> CreateOptions(string connectionString)
	{
		connectionString = SqliteConnectionStrings.WithForeignKeys(connectionString);
		return new(new DataOptions().UseSQLite(connectionString).WithDurability(SqliteTier.Durable));
	}
}
