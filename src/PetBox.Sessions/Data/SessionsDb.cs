using LinqToDB;
using LinqToDB.Data;
using PetBox.Core.Data;

namespace PetBox.Sessions.Data;

// linq2db context over a project's sessions file (data/sessions/{project}.db).
// Unlike tasks/memory, sessions are not named containers: one file per project,
// many session rows keyed by agent-supplied sessionId.
public sealed class SessionsDb : DataConnection
{
	public SessionsDb(DataOptions<SessionsDb> options) : base(options.Options) { }

	public ITable<SessionRow> Sessions => this.GetTable<SessionRow>();

	public ITable<MessageVec> MessageVectors => this.GetTable<MessageVec>();

	// DURABLE, not Telemetry — the one tier assignment in the sweep that is not obvious, so the
	// reason is recorded here rather than left to be re-litigated. Sessions sit next to logs in the
	// "high write volume, machine-generated" intuition, but nothing else about them matches: a
	// SessionRow is agent-authored content written through an MCP call that RETURNED SUCCESS, kept
	// under soft-delete, and read back later by session_search as the authoritative record of what
	// was decided. That is the same contract tasks and memory have. MessageVectors alongside them
	// are derived and regenerable, but they do not get their own file, and the tier is set by its
	// most expensive loss, not its cheapest.
	public static DataOptions<SessionsDb> CreateOptions(string connectionString) =>
		new(new DataOptions().UseSQLite(connectionString).WithDurability(SqliteTier.Durable));
}
