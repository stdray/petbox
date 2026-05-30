using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;

namespace PetBox.Tasks.Data;

// linq2db context over a single task board file (data/tasks/{project}/{board}.db).
public sealed class TasksDb : DataConnection
{
	public TasksDb(DataOptions<TasksDb> options) : base(options.Options) { }

	public ITable<PlanNode> PlanNodes => this.GetTable<PlanNode>();

	public static DataOptions<TasksDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
