using LinqToDB;
using LinqToDB.Async;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data.Contract;
using PetBox.Data.Schema;

namespace PetBox.Data.Services;

// The one executor for raw user SQL: verifies the DataDb exists, resolves its
// connection string, binds parameters, and runs the reader / non-query / migration.
// The PRAGMA deny-list lives here so both the MCP tools and the REST endpoints enforce it,
// and every path opens its connection through the same quota'd OpenAsync below.
public sealed class DataSqlService : IDataSqlService
{
	// PRAGMAs that escape the DB file or corrupt shared state. Default-allow otherwise,
	// keeping the raw-SQL pass-through promise (cheaper to maintain than an allow-list).
	// max_page_count is here because it IS the disk quota: it's per-connection state we
	// re-apply on every open, so a pet raising it mid-request would lift its own quota.
	static readonly HashSet<string> PragmaDenyList = new(StringComparer.OrdinalIgnoreCase)
	{
		"writable_schema", "temp_store_directory", "data_store_directory", "trusted_schema",
		"max_page_count",
	};

	// The core-db lookup (the DataDbs catalog row + its quota) goes through the FACTORY: a fresh,
	// caller-owned connection per open, never a request-shared one. A linq2db DataConnection is not
	// thread-safe, and a single request can run several queries concurrently.
	readonly ICoreDbFactory _core;
	readonly IDataDbFactory _factory;
	readonly SchemaRunner _runner;

	public DataSqlService(ICoreDbFactory core, IDataDbFactory factory, SchemaRunner runner)
	{
		_core = core;
		_factory = factory;
		_runner = runner;
	}

	public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
		string projectKey, string dbName, string sql, IReadOnlyList<SqlArg> parameters, int timeoutSeconds, CancellationToken ct = default)
	{
		await using var conn = await OpenAsync(projectKey, dbName, ct);
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.CommandTimeout = timeoutSeconds;
		Bind(cmd, parameters);

		var rows = new List<IReadOnlyDictionary<string, object?>>();
		await using var reader = await cmd.ExecuteReaderAsync(ct);
		var fieldCount = reader.FieldCount;
		while (await reader.ReadAsync(ct))
		{
			var row = new Dictionary<string, object?>(fieldCount, StringComparer.Ordinal);
			for (var i = 0; i < fieldCount; i++)
				row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
			rows.Add(row);
		}
		return rows;
	}

	public async Task<int> ExecAsync(
		string projectKey, string dbName, string sql, IReadOnlyList<SqlArg> parameters, int timeoutSeconds, CancellationToken ct = default)
	{
		if (IsBlockedPragma(sql, out var denied))
			throw new DeniedPragmaException(denied!);

		await using var conn = await OpenAsync(projectKey, dbName, ct);
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.CommandTimeout = timeoutSeconds;
		Bind(cmd, parameters);

		return await cmd.ExecuteNonQueryAsync(ct);
	}

	public async Task<SchemaApplyResult> ApplySchemaAsync(
		string projectKey, string dbName, string name, string sql, CancellationToken ct = default)
	{
		// Migration SQL writes, so it runs on the same quota'd connection as exec.
		await using var conn = await OpenAsync(projectKey, dbName, ct);
		return _runner.Apply(conn, name, sql);
	}

	// Verifies the DataDb exists and opens a connection with its size quota applied.
	// PRAGMA max_page_count is per-connection state, so the quota from the DataDbs row
	// has to be re-applied here on every request — see IDataDbFactory.
	async Task<SqliteConnection> OpenAsync(string projectKey, string dbName, CancellationToken ct)
	{
		DataDb? row;
		using (var core = _core.Open())
		{
			row = await core.DataDbs.FirstOrDefaultAsync(
				(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		}
		if (row is null) throw new DataDbNotFoundException(projectKey, dbName);
		return await _factory.OpenAsync(projectKey, dbName, row.MaxPageCount, ct);
	}

	static void Bind(SqliteCommand cmd, IReadOnlyList<SqlArg> parameters)
	{
		foreach (var arg in parameters)
		{
			var p = cmd.CreateParameter();
			p.ParameterName = arg.Name;
			p.Value = arg.Value ?? DBNull.Value;
			cmd.Parameters.Add(p);
		}
	}

	static bool IsBlockedPragma(string sql, out string? name)
	{
		name = null;
		var trimmed = sql.TrimStart();
		if (!trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)) return false;
		var rest = trimmed.AsSpan(6).TrimStart();
		var end = 0;
		while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '_')) end++;
		if (end == 0) return false;
		var pragmaName = rest[..end].ToString();
		if (PragmaDenyList.Contains(pragmaName)) { name = pragmaName; return true; }
		return false;
	}
}
