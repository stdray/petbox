using System.Data;
using FluentMigrator;
using PetBox.Sessions.Contract;

namespace PetBox.Sessions.Data.Migrations;

// Drop the SCD-2 temporal storage for sessions -> a single flat latest-snapshot row per
// session, content stored as a Brotli-compressed JSONL message blob (see SessionContent).
//
// Each session's LATEST active revision is carried over as one "legacy" message; per-revision
// history is intentionally dropped (it was N full ~74KB copies per session — the bloat this
// removes). Active sessions also self-heal on the next full-transcript push, and the raw
// per-turn archive lives in the local Claude JSONL transcripts.
//
// SQLite cannot ALTER the temporal shape into the flat one, so this is the table-REBUILD idiom:
// rename the old table aside, create the new one, move the rows, drop the old. Rename/Create/Delete
// are all expressible with the typed API — no raw DDL is needed here at all. The row move is NOT
// SQL: the payload has to be re-encoded (Brotli) in C#, so it stays an Execute.WithConnection.
// Expressions run in the order Up() queues them, which is what makes rename-before-create safe.
//
// No `IF EXISTS` anywhere: `sessions` is created by M001 and `sessions_legacy` by the RENAME two
// lines up, both of which VersionInfo guarantees have run. If either is missing, that is schema
// drift and this migration SHOULD fail loudly rather than shrug and leave the data behind.
[Migration(2, "Flatten sessions to latest-snapshot (JSONL+Brotli), drop temporal history")]
public sealed class M002_FlattenSessions : Migration
{
	public override void Up()
	{
		Rename.Table("sessions").To("sessions_legacy");

		Create.Table("sessions")
			.WithColumn("SessionId").AsString().NotNullable().PrimaryKey()
			.WithColumn("Agent").AsString().NotNullable()
			.WithColumn("ContentZ").AsBinary().NotNullable()
			.WithColumn("Version").AsInt64().NotNullable()
			.WithColumn("Updated").AsString().NotNullable();

		// Carry over each session's latest active revision as a single legacy message.
		Execute.WithConnection((conn, tx) =>
		{
			var legacy = new List<(string Id, string Agent, string Content, string Updated)>();
			using (var read = conn.CreateCommand())
			{
				read.Transaction = tx;
				read.CommandText = "SELECT Key, Agent, Content, Updated FROM sessions_legacy WHERE ActiveTo IS NULL;";
				using var rd = read.ExecuteReader();
				while (rd.Read())
					legacy.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3)));
			}

			foreach (var (id, agent, content, updated) in legacy)
			{
				var blob = SessionContent.Encode(new[] { new SessionMessage(1, "legacy", content) });
				using var ins = conn.CreateCommand();
				ins.Transaction = tx;
				ins.CommandText = "INSERT INTO sessions (SessionId, Agent, ContentZ, Version, Updated) VALUES (@id, @agent, @z, 1, @updated);";
				AddParam(ins, "@id", id);
				AddParam(ins, "@agent", agent);
				AddParam(ins, "@z", blob);
				AddParam(ins, "@updated", updated);
				ins.ExecuteNonQuery();
			}
		});

		Delete.Table("sessions_legacy");
	}

	static void AddParam(IDbCommand cmd, string name, object value)
	{
		var p = cmd.CreateParameter();
		p.ParameterName = name;
		p.Value = value;
		cmd.Parameters.Add(p);
	}

	// One-way: the flattening destroyed the per-revision history, so Down() can only remove the
	// flat table (it does not resurrect what M001 held). No `IF EXISTS` — Down() runs after Up().
	public override void Down() => Delete.Table("sessions");
}
