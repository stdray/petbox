using LinqToDB;
using LinqToDB.Async;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data.Contract;

namespace PetBox.Data.Services;

// The one executor for raw user SQL: verifies the DataDb exists, resolves its
// connection string, binds parameters, and runs the reader / non-query. The PRAGMA
// deny-list lives here so both the MCP tools and the REST endpoints enforce it.
public sealed class DataSqlService : IDataSqlService
{
	// PRAGMAs that escape the DB file or corrupt shared state. Default-allow otherwise,
	// keeping the raw-SQL pass-through promise (cheaper to maintain than an allow-list).
	static readonly HashSet<string> PragmaDenyList = new(StringComparer.OrdinalIgnoreCase)
	{
		"writable_schema", "temp_store_directory", "data_store_directory", "trusted_schema",
	};

	readonly PetBoxDb _db;
	readonly IDataDbFactory _factory;

	public DataSqlService(PetBoxDb db, IDataDbFactory factory)
	{
		_db = db;
		_factory = factory;
	}

	public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
		string projectKey, string dbName, string sql, IReadOnlyList<SqlArg> parameters, int timeoutSeconds, CancellationToken ct = default)
	{
		var cs = await ResolveConnectionStringAsync(projectKey, dbName, ct);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync(ct);
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

		var cs = await ResolveConnectionStringAsync(projectKey, dbName, ct);
		await using var conn = new SqliteConnection(cs);
		await conn.OpenAsync(ct);
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.CommandTimeout = timeoutSeconds;
		Bind(cmd, parameters);

		return await cmd.ExecuteNonQueryAsync(ct);
	}

	async Task<string> ResolveConnectionStringAsync(string projectKey, string dbName, CancellationToken ct)
	{
		var row = await _db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == dbName, ct);
		if (row is null) throw new DataDbNotFoundException(projectKey, dbName);
		return _factory.GetConnectionString(projectKey, dbName);
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
