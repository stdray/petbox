using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;

namespace PetBox.Core.Search;

// Durable IIndexCursorStore co-located in the entity's SQLite file: the version cursor and the
// per-entity attempt/dead-letter state for each Class-B index ride the same file as the data they
// enrich, so a single backup/restore of that file carries the vectorization progress with it.
// In-memory state (InMemoryIndexCursorStore) is for tests; this is what the memory/tasks retrofit
// wires into the worker. Schema (search_cursor + search_deadletter) is created by each module's
// migration. Each call opens a fresh short-lived connection — SQLite WAL serialises the writer.
public sealed class SqliteIndexCursorStore : IIndexCursorStore
{
	readonly Func<DataConnection> _connect;

	// connect: opens a fresh DataConnection to the SAME file the index lives in.
	public SqliteIndexCursorStore(Func<DataConnection> connect) => _connect = connect;

	public static void EnsureSchema(DataConnection db) => db.Execute("""
		CREATE TABLE IF NOT EXISTS search_cursor (
			IndexName TEXT PRIMARY KEY, Version INTEGER NOT NULL
		);
		CREATE TABLE IF NOT EXISTS search_deadletter (
			IndexName TEXT NOT NULL, Type TEXT NOT NULL, Id TEXT NOT NULL,
			Attempts INTEGER NOT NULL, Dead INTEGER NOT NULL,
			PRIMARY KEY (IndexName, Type, Id)
		);
		""");

	public async Task<long> GetCursorAsync(string index, CancellationToken ct = default)
	{
		using var db = _connect();
		return await db.GetTable<CursorRow>()
			.Where(r => r.IndexName == index)
			.Select(r => (long?)r.Version)
			.FirstOrDefaultAsync(ct) ?? 0;
	}

	public async Task SetCursorAsync(string index, long version, CancellationToken ct = default)
	{
		using var db = _connect();
		await db.InsertOrReplaceAsync(new CursorRow { IndexName = index, Version = version }, token: ct);
	}

	public async Task<int> BumpAttemptsAsync(string index, string type, string id, CancellationToken ct = default)
	{
		using var db = _connect();
		var row = await Find(db, index, type, id, ct);
		var attempts = (row?.Attempts ?? 0) + 1;
		await db.InsertOrReplaceAsync(new DeadLetterRow
		{
			IndexName = index, Type = type, Id = id, Attempts = attempts, Dead = row?.Dead ?? false,
		}, token: ct);
		return attempts;
	}

	// Success clears the transient-failure trail — but never resurrects a dead-lettered item
	// (the worker already skips dead items, so a clear here would only matter on manual revival).
	public async Task ClearAttemptsAsync(string index, string type, string id, CancellationToken ct = default)
	{
		using var db = _connect();
		await db.GetTable<DeadLetterRow>()
			.Where(r => r.IndexName == index && r.Type == type && r.Id == id && !r.Dead)
			.DeleteAsync(ct);
	}

	public async Task MarkDeadAsync(string index, string type, string id, CancellationToken ct = default)
	{
		using var db = _connect();
		var row = await Find(db, index, type, id, ct);
		await db.InsertOrReplaceAsync(new DeadLetterRow
		{
			IndexName = index, Type = type, Id = id, Attempts = row?.Attempts ?? 0, Dead = true,
		}, token: ct);
	}

	public async Task<bool> IsDeadAsync(string index, string type, string id, CancellationToken ct = default)
	{
		using var db = _connect();
		return await db.GetTable<DeadLetterRow>()
			.AnyAsync(r => r.IndexName == index && r.Type == type && r.Id == id && r.Dead, ct);
	}

	static Task<DeadLetterRow?> Find(DataConnection db, string index, string type, string id, CancellationToken ct) =>
		db.GetTable<DeadLetterRow>()
			.Where(r => r.IndexName == index && r.Type == type && r.Id == id)
			.FirstOrDefaultAsync(ct)!;

	[Table("search_cursor")]
	sealed class CursorRow
	{
		[Column, PrimaryKey] public string IndexName { get; set; } = string.Empty;
		[Column] public long Version { get; set; }
	}

	[Table("search_deadletter")]
	sealed class DeadLetterRow
	{
		[Column, PrimaryKey(0)] public string IndexName { get; set; } = string.Empty;
		[Column, PrimaryKey(1)] public string Type { get; set; } = string.Empty;
		[Column, PrimaryKey(2)] public string Id { get; set; } = string.Empty;
		[Column] public int Attempts { get; set; }
		[Column] public bool Dead { get; set; }
	}
}
