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
[Migration(2, "Flatten sessions to latest-snapshot (JSONL+Brotli), drop temporal history")]
public sealed class M002_FlattenSessions : Migration
{
	public override void Up()
	{
		// SQLite can't ALTER the temporal shape into the flat one — rebuild the table.
		Execute.Sql("ALTER TABLE sessions RENAME TO sessions_legacy;");
		Execute.Sql("""
			CREATE TABLE sessions (
				SessionId TEXT    NOT NULL PRIMARY KEY,
				Agent     TEXT    NOT NULL,
				ContentZ  BLOB    NOT NULL,
				Version   INTEGER NOT NULL,
				Updated   TEXT    NOT NULL
			);
			""");

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

		Execute.Sql("DROP TABLE sessions_legacy;");
	}

	static void AddParam(IDbCommand cmd, string name, object value)
	{
		var p = cmd.CreateParameter();
		p.ParameterName = name;
		p.Value = value;
		cmd.Parameters.Add(p);
	}

	public override void Down() =>
		Execute.Sql("DROP TABLE IF EXISTS sessions;");
}
