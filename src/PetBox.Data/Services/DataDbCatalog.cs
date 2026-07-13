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
public sealed class DataDbCatalog(ICoreDbFactory core, IDataDbFactory factory) : IDataDbCatalog
{
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

		var quota = maxPageCount ?? DataDbFactory.DefaultMaxPageCount;

		using (var db = core.Open())
		{
			if (await db.DataDbs.AnyAsync((DataDb d) => d.ProjectKey == projectKey && d.Name == name, ct))
				return new DataDbChangeResult.Conflict($"DataDb '{name}' already exists");
		}

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
}
