using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Engine.Output;
using Microsoft.Data.Sqlite;

namespace PetBox.Data.Schema;

// Applies a single named SQL migration script against a DataDb, with
// hash-based idempotency.
//
// Flow when a pet POSTs { name, sql } to /api/data/{p}/{db}/schema:
//
//   1. Compute SqlNormalizer.Hash(sql).
//   2. SELECT Hash FROM __SchemaVersions WHERE ScriptName = @name
//      a. row matches name + hash    → AlreadyApplied (HTTP 200, no-op)
//      b. row matches name, diff hash → Conflict (HTTP 409 with existing hash)
//      c. no row                     → proceed
//   3. Build a DbUp UpgradeEngine with a single-script provider and
//      SqliteHashingJournal, call PerformUpgrade().
//      - SQLite serializes writes; a concurrent migrator at the same time will
//        block on the file lock and, when unblocked, find the row already in
//        the journal → returns AlreadyApplied naturally.
//   4. Map result → SchemaApplyResult { kind, hash, error? }.
//
// The journal table name (`__SchemaVersions`) lives inside the DataDb file
// itself, not in main PetBoxDb. Each DataDb owns its own migration history.
public sealed class SchemaRunner
{
	public const string JournalTableName = "__SchemaVersions";

	public SchemaApplyResult Apply(string connectionString, string name, string sql)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(sql);

		string newHash;
		try { newHash = SqlNormalizer.Hash(sql); }
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Parse failure (unparseable SQL) — no hash to compare. Pet's script
			// is malformed; return Failed without touching the journal.
			return SchemaApplyResult.Failed("SQL parse error: " + ex.Message, hash: "");
		}

		var existing = ReadExistingHash(connectionString, name);
		if (existing is not null)
		{
			return existing == newHash
				? SchemaApplyResult.AlreadyApplied(newHash)
				: SchemaApplyResult.Conflict(existingHash: existing, providedHash: newHash);
		}

		try
		{
			var script = new SqlScript(name, sql);
			var engine = DeployChanges.To
				.SqliteDatabase(connectionString)
				.WithScripts(script)
				.JournalTo((connMgr, log) => new SqliteHashingJournal(connMgr, log, JournalTableName))
				.WithTransactionPerScript()
				.LogToNowhere()
				.Build();

			var result = engine.PerformUpgrade();

			return result.Successful
				? SchemaApplyResult.Applied(newHash)
				: SchemaApplyResult.Failed(result.Error?.Message ?? "Unknown failure", newHash);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return SchemaApplyResult.Failed(ex.Message, newHash);
		}
	}

	static string? ReadExistingHash(string connectionString, string scriptName)
	{
		using var conn = new SqliteConnection(connectionString);
		conn.Open();

		// The journal table may not exist yet — first script application creates it.
		using var existsCmd = conn.CreateCommand();
		existsCmd.CommandText =
			$"SELECT name FROM sqlite_master WHERE type='table' AND name='{JournalTableName}'";
		using var existsReader = existsCmd.ExecuteReader();
		if (!existsReader.Read()) return null;
		existsReader.Close();

		using var cmd = conn.CreateCommand();
		cmd.CommandText = $"SELECT Hash FROM {JournalTableName} WHERE ScriptName = @name LIMIT 1";
		var p = cmd.CreateParameter();
		p.ParameterName = "@name";
		p.Value = scriptName;
		cmd.Parameters.Add(p);

		var result = cmd.ExecuteScalar();
		return result as string;
	}
}

public enum SchemaApplyKind
{
	Applied,
	AlreadyApplied,
	Conflict,
	Failed,
}

public sealed record SchemaApplyResult(SchemaApplyKind Kind, string Hash, string? ExistingHash = null, string? Error = null)
{
	public static SchemaApplyResult Applied(string hash) => new(SchemaApplyKind.Applied, hash);
	public static SchemaApplyResult AlreadyApplied(string hash) => new(SchemaApplyKind.AlreadyApplied, hash);
	public static SchemaApplyResult Conflict(string existingHash, string providedHash) =>
		new(SchemaApplyKind.Conflict, providedHash, ExistingHash: existingHash);
	public static SchemaApplyResult Failed(string error, string hash) =>
		new(SchemaApplyKind.Failed, hash, Error: error);
}
