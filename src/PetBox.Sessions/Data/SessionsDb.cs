using LinqToDB;
using LinqToDB.Data;
using LinqToDB.DataProvider.SQLite;

namespace PetBox.Sessions.Data;

// linq2db context over a project's sessions file (data/sessions/{project}.db).
// Unlike tasks/memory, sessions are not named containers: one file per project,
// many session rows keyed by agent-supplied sessionId.
public sealed class SessionsDb : DataConnection
{
	public SessionsDb(DataOptions<SessionsDb> options) : base(options.Options) { }

	public ITable<SessionRow> Sessions => this.GetTable<SessionRow>();

	public static DataOptions<SessionsDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString));
}
