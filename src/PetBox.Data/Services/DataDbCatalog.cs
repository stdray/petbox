using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data.Contract;
using PetBox.Data.Schema;

namespace PetBox.Data.Services;

// The catalog behind IDataDbCatalog: the DataDbs rows in core.db + the SQLite file each one owns.
// Core.db is reached through the FACTORY, opened inside this service and nowhere else, and each open
// is short: the file create/delete calls of IDataDbFactory happen with no core connection held (core
// runs Cache=Shared, and a SQLITE_LOCKED raised under a held connection is not retried).
//
// The DataDb NAME rules live HERE, not in any adapter: a DataDb name becomes an on-disk file name
// ({baseDir}/{projectKey}/{name}.db) and a SQL identifier, and identifiers cannot be parameterized —
// this validation IS the injection/path-traversal defence, and it must hold for EVERY caller of the
// catalog (REST, pages, MCP db_create), not just whichever adapter remembered to check.
public sealed partial class DataDbCatalog(ICoreDbFactory core, IDataDbFactory factory) : IDataDbCatalog
{
	// The SQLite reserved-name spec settled in plan: starts with a-z, then a-z / 0-9 / '_' / '-',
	// 100 chars total max. (Moved from the REST endpoint — see the class comment.)
	[GeneratedRegex(@"^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex DbNameRegex();

	// Names the Data module claims for itself inside a DataDb. Kept case-insensitive and kept even
	// though the regex already rejects a leading '_' — belt and braces on an injection surface.
	static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"__schema_versions",
	};

	public async Task<IReadOnlyList<DataDbInfo>> ListAsync(string projectKey, CancellationToken ct = default)
	{
		using var db = core.Open();
		return await db.DataDbs
			.Where(d => d.ProjectKey == projectKey)
			.OrderBy(d => d.Name)
			.Select(d => new DataDbInfo(d.Name, d.Description, d.MaxPageCount, d.CreatedAt, d.UpdatedAt))
			.ToListAsync(ct);
	}

	public async Task<DataDbInfo?> GetAsync(string projectKey, string name, CancellationToken ct = default)
	{
		using var db = core.Open();
		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == projectKey && d.Name == name, ct);
		return row is null ? null : new DataDbInfo(row.Name, row.Description, row.MaxPageCount, row.CreatedAt, row.UpdatedAt);
	}

	public async Task<DataDbChangeResult> CreateAsync(
		string projectKey, string name, string? description, long? maxPageCount, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(name))
			return new DataDbChangeResult.Refused("name is required");
		if (!DbNameRegex().IsMatch(name))
			return new DataDbChangeResult.Refused("invalid name; must match ^[a-z][a-z0-9_-]{0,99}$");
		if (ReservedNames.Contains(name))
			return new DataDbChangeResult.Refused($"'{name}' is reserved");

		var quota = maxPageCount ?? DataDbFactory.DefaultMaxPageCount;

		using (var db = core.Open())
		{
			// petbox is the source of truth for project identity: no DataDbs row may name a project
			// that is not in Projects. (Adapters map this NotFound — REST: 404 "project not found".)
			if (!await db.Projects.AnyAsync((Project p) => p.Key == projectKey, ct))
				return new DataDbChangeResult.NotFound();
			if (await db.DataDbs.AnyAsync((DataDb d) => d.ProjectKey == projectKey && d.Name == name, ct))
				return new DataDbChangeResult.Conflict($"DataDb '{name}' already exists");
		}

		// Below this the quota silently would not exist: max_page_count is a page COUNT, and 1024
		// pages at 4 KB is the 4 MB floor the REST surface always enforced.
		if (quota < 1024)
			return new DataDbChangeResult.Refused("maxPageCount must be >= 1024 (4 MB at 4KB pages)");

		// The file first, then the row: a row whose file failed to materialize would advertise a DataDb
		// nothing can open. (The reverse leaves a file with no row — an orphan the cleanup service sweeps.)
		await factory.CreateAsync(projectKey, name, quota, ct);

		var now = DateTime.UtcNow;
		using (var db = core.Open())
		{
			await db.InsertAsync(new DataDb
			{
				ProjectKey = projectKey,
				Name = name,
				Description = description,
				MaxPageCount = quota,
				CreatedAt = now,
				UpdatedAt = now,
			}, token: ct);
		}

		return new DataDbChangeResult.Created(new DataDbInfo(name, description, quota, now, now));
	}

	public async Task<DataDbChangeResult> DeleteAsync(string projectKey, string name, CancellationToken ct = default)
	{
		int deleted;
		using (var db = core.Open())
		{
			// The project is part of the ADDRESS: a call naming another project's DataDb matches no row.
			deleted = await db.DataDbs
				.Where(d => d.ProjectKey == projectKey && d.Name == name)
				.DeleteAsync(ct);
		}
		if (deleted == 0) return new DataDbChangeResult.NotFound();

		// Best-effort file removal (a locked file is swept by OrphanCleanupService on its next tick —
		// the row is already gone, so the (project, name) slot is free immediately).
		factory.TryDelete(projectKey, name);
		return new DataDbChangeResult.Deleted();
	}

	public async Task<IReadOnlyList<DataDbTableInfo>?> DescribeAsync(
		string projectKey, string name, CancellationToken ct = default)
	{
		// Existence is proven against the CATALOG row of THIS project — the connection string is derived
		// from (project, name), so describing without this check would let any project's key introspect
		// any other project's file by naming it.
		if (await GetAsync(projectKey, name, ct) is null) return null;

		await using var conn = new SqliteConnection(factory.GetConnectionString(projectKey, name));
		await conn.OpenAsync(ct);

		var names = new List<string>();
		await using (var cmd = conn.CreateCommand())
		{
			cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' "
				+ "AND name NOT LIKE 'sqlite_%' AND name <> @journal ORDER BY name";
			var p = cmd.CreateParameter();
			p.ParameterName = "@journal";
			p.Value = SchemaRunner.JournalTableName;
			cmd.Parameters.Add(p);
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
		}

		var tables = new List<DataDbTableInfo>();
		foreach (var tableName in names)
		{
			var cols = new List<DataDbColumnInfo>();
			await using var cmd = conn.CreateCommand();
			cmd.CommandText = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			while (await reader.ReadAsync(ct))
				cols.Add(new DataDbColumnInfo(reader.GetString(1), reader.GetString(2), reader.GetInt32(3) == 1, reader.GetInt32(5) > 0));
			tables.Add(new DataDbTableInfo(tableName, cols));
		}
		return tables;
	}

	public async Task<IReadOnlyList<DataDbMigrationInfo>?> ListMigrationsAsync(
		string projectKey, string name, CancellationToken ct = default)
	{
		// Same address rule as DescribeAsync: existence is proven against THIS project's catalog row,
		// so no project can introspect another project's file by naming it. The row also carries the
		// quota, which factory.OpenAsync re-applies (per-connection state) like every other open.
		var row = await GetAsync(projectKey, name, ct);
		if (row is null) return null;

		await using var conn = await factory.OpenAsync(projectKey, name, row.MaxPageCount, ct);

		// __SchemaVersions may not exist yet if no migrations have been applied. The table name is
		// the SchemaRunner CONSTANT, never caller input — the only identifier interpolated here.
		await using (var existsCmd = conn.CreateCommand())
		{
			existsCmd.CommandText =
				$"SELECT name FROM sqlite_master WHERE type='table' AND name='{SchemaRunner.JournalTableName}'";
			if (await existsCmd.ExecuteScalarAsync(ct) is null) return [];
		}

		await using var cmd = conn.CreateCommand();
		cmd.CommandText =
			$"SELECT SchemaVersionID, ScriptName, Applied, Hash FROM {SchemaRunner.JournalTableName} ORDER BY SchemaVersionID";
		await using var reader = await cmd.ExecuteReaderAsync(ct);
		var entries = new List<DataDbMigrationInfo>();
		while (await reader.ReadAsync(ct))
		{
			entries.Add(new DataDbMigrationInfo(
				Id: reader.GetInt64(0),
				ScriptName: reader.GetString(1),
				Applied: reader.GetDateTime(2),
				Hash: reader.GetString(3)));
		}
		return entries;
	}
}
