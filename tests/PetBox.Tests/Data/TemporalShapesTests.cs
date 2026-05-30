using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Mapping;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data.Temporal;

namespace PetBox.Tests.Data;

// Proves the generic engine generalises past plans: a Session (single markdown
// blob keyed by sessionId) and a Memory note (description+body+tags keyed by
// name). Same engine, same concurrency semantics, different payloads.

[Collection("DataModule")]
public sealed class TemporalSessionTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public TemporalSessionTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-session-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "session.db")}";
		EnsureSchema(_cs);
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Save_Then_Edit_KeepsRevisions()
	{
		await Save(new SessionRow { Key = "s1", Agent = "claude-code", Content = "# plan v1" });
		var r = await Save(new SessionRow { Key = "s1", Version = 1, Agent = "claude-code", Content = "# plan v2" });

		r.Applied.Should().BeTrue();
		r.Closed.Should().Be(1);
		Active("s1")!.Content.Should().Be("# plan v2");
		All("s1").Should().HaveCount(2);
	}

	[Fact]
	public async Task Resave_SameBlob_IsNoOp()
	{
		await Save(new SessionRow { Key = "s1", Agent = "a", Content = "x" });
		var r = await Save(new SessionRow { Key = "s1", Version = 1, Agent = "a", Content = "x" });

		r.Inserted.Should().Be(0);
		All("s1").Should().HaveCount(1);
	}

	[Fact]
	public async Task ConcurrentSave_OnSameSession_Conflicts()
	{
		await Save(new SessionRow { Key = "s1", Agent = "a", Content = "v1" });
		await Save(new SessionRow { Key = "s1", Version = 1, Agent = "a", Content = "by-laptop" });  // -> v2

		// desktop still believes baseline is v1 (slug/session collision across machines)
		var r = await Save(new SessionRow { Key = "s1", Version = 1, Agent = "a", Content = "by-desktop" });

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Kind == TemporalConflictKind.Stale);
		Active("s1")!.Content.Should().Be("by-laptop");
	}

	async Task<TemporalUpsertResult> Save(SessionRow row)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return await TemporalStore.UpsertAsync(db, new[] { row });
	}

	List<SessionRow> All(string key)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return db.GetTable<SessionRow>().Where(x => x.Key == key).OrderBy(x => x.Version).ToList();
	}

	SessionRow? Active(string key)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return db.GetTable<SessionRow>().Where(x => x.Key == key && x.ActiveTo == null).ToList().FirstOrDefault();
	}

	static void EnsureSchema(string cs)
	{
		using var c = new SqliteConnection(cs);
		c.Open();
		using var cmd = c.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS Session (
				Key        TEXT    NOT NULL,
				Version    INTEGER NOT NULL,
				Agent      TEXT    NOT NULL,
				Content    TEXT    NOT NULL,
				ActiveFrom INTEGER NOT NULL,
				ActiveTo   INTEGER,
				Created    TEXT    NOT NULL,
				Updated    TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			""";
		cmd.ExecuteNonQuery();
	}
}

[Collection("DataModule")]
public sealed class TemporalMemoryTests : IDisposable
{
	readonly string _dir;
	readonly string _cs;

	public TemporalMemoryTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memory-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		_cs = $"Data Source={Path.Combine(_dir, "memory.db")}";
		EnsureSchema(_cs);
	}

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task TagsChange_CreatesRevision()
	{
		await Up(new MemoryRow { Key = "feedback-x", Description = "d", Body = "b", Tags = "a,b" });
		var r = await Up(new MemoryRow { Key = "feedback-x", Version = 1, Description = "d", Body = "b", Tags = "a,b,c" });

		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(1);
		Active("feedback-x")!.Tags.Should().Be("a,b,c");
	}

	[Fact]
	public async Task StaleEdit_OnSameNote_Conflicts()
	{
		await Up(new MemoryRow { Key = "feedback-x", Description = "d", Body = "v1", Tags = "t" });
		await Up(new MemoryRow { Key = "feedback-x", Version = 1, Description = "d", Body = "by-B", Tags = "t" }); // -> v2

		var r = await Up(new MemoryRow { Key = "feedback-x", Version = 1, Description = "d", Body = "by-A", Tags = "t" });

		r.Applied.Should().BeFalse();
		r.Conflicts.Should().ContainSingle(c => c.Kind == TemporalConflictKind.Stale);
		Active("feedback-x")!.Body.Should().Be("by-B");
	}

	async Task<TemporalUpsertResult> Up(MemoryRow row)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return await TemporalStore.UpsertAsync(db, new[] { row });
	}

	MemoryRow? Active(string key)
	{
		using var db = new DataConnection(new DataOptions().UseSQLite(_cs));
		return db.GetTable<MemoryRow>().Where(x => x.Key == key && x.ActiveTo == null).ToList().FirstOrDefault();
	}

	static void EnsureSchema(string cs)
	{
		using var c = new SqliteConnection(cs);
		c.Open();
		using var cmd = c.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS Memory (
				Key         TEXT    NOT NULL,
				Version     INTEGER NOT NULL,
				Description TEXT    NOT NULL,
				Body        TEXT    NOT NULL,
				Tags        TEXT    NOT NULL,
				ActiveFrom  INTEGER NOT NULL,
				ActiveTo    INTEGER,
				Created     TEXT    NOT NULL,
				Updated     TEXT    NOT NULL,
				PRIMARY KEY (Key, Version)
			);
			""";
		cmd.ExecuteNonQuery();
	}
}

// Session payload: a per-session markdown blob (what claude-code writes to
// ~/.claude/plans/*.md), keyed by a server-generated sessionId.
[Table("Session")]
public sealed record SessionRow : TemporalRow
{
	[Column, NotNull] public string Agent { get; init; } = string.Empty;
	[Column, NotNull] public string Content { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is SessionRow s && s.Agent == Agent && s.Content == Content;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}

// Memory payload: a markdown note with free-form CSV tags, keyed by name within
// a per-scope DB (memory/$global.db, memory/{ws}.db, memory/{project}.db).
[Table("Memory")]
public sealed record MemoryRow : TemporalRow
{
	[Column, NotNull] public string Description { get; init; } = string.Empty;
	[Column, NotNull] public string Body { get; init; } = string.Empty;
	[Column, NotNull] public string Tags { get; init; } = string.Empty;

	public override bool SamePayload(TemporalRow other) =>
		other is MemoryRow m && m.Description == Description && m.Body == Body && m.Tags == Tags;

	public override TemporalRow AsRevision(long version, DateTime created, DateTime updated) =>
		this with { Version = version, ActiveFrom = version, ActiveTo = null, Created = created, Updated = updated };
}
