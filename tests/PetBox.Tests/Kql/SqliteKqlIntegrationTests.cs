using System.Collections.Immutable;
using System.Text.Json;
using Kusto.Language;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Log.Core;

namespace PetBox.Tests.Kql;

public sealed class SqliteKqlIntegrationTests : IAsyncLifetime
{
	readonly string _tempDir;
	readonly string _dbPath;
	LogDb _logDb = null!;

	public SqliteKqlIntegrationTests()
	{
		_tempDir = Path.Combine(Path.GetTempPath(), "petbox-kql-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_tempDir);
		_dbPath = Path.Combine(_tempDir, "test.db");
	}

	public async Task InitializeAsync()
	{
		_logDb = new LogDb(LogDb.CreateOptions($"Data Source={_dbPath};Cache=Shared"));

		await using var conn = new SqliteConnection($"Data Source={_dbPath};Cache=Shared");
		await conn.OpenAsync();
		await using var cmd = conn.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE LogEntries (
				Id INTEGER PRIMARY KEY AUTOINCREMENT,
				ServiceKey TEXT NOT NULL,
				TimestampMs INTEGER NOT NULL,
				Level INTEGER NOT NULL,
				Message TEXT NOT NULL,
				MessageTemplate TEXT NOT NULL,
				Exception TEXT,
				PropertiesJson TEXT NOT NULL DEFAULT '{}',
				TemplateHash INTEGER NOT NULL DEFAULT 0
			);
			CREATE INDEX IX_LogEntries_ServiceKey_TimestampMs ON LogEntries(ServiceKey, TimestampMs DESC);
			CREATE INDEX IX_LogEntries_TimestampMs ON LogEntries(TimestampMs DESC);
			CREATE INDEX IX_LogEntries_Level ON LogEntries(Level);
			""";
		await cmd.ExecuteNonQueryAsync();

		await _logDb.LogEntries.BulkCopyAsync(Seed.Select(ToRecord));
	}

	public async Task DisposeAsync()
	{
		_logDb?.Dispose();
		try { Directory.Delete(_tempDir, recursive: true); }
		catch { /* best effort */ }
	}

	static readonly IReadOnlyList<LogEntryCandidate> Seed =
	[
		Mk(1, LogLevel.Information, "hello world", "svc-a"),
		Mk(2, LogLevel.Error, "boom", "svc-b"),
		Mk(3, LogLevel.Warning, "meh", "svc-a"),
		Mk(4, LogLevel.Error, "crash on Earth", "svc-c"),
		Mk(5, LogLevel.Debug, "starting", "svc-c"),
	];

	static LogEntryCandidate Mk(int secs, LogLevel level, string msg, string serviceKey,
		ImmutableDictionary<string, JsonElement>? props = null) => new()
		{
			Timestamp = new DateTime(2026, 4, 19, 10, secs, 0, DateTimeKind.Utc),
			Level = level,
			Message = msg,
			MessageTemplate = msg,
			ServiceKey = serviceKey,
			Properties = props is not null
				? PropertiesJsonSerializer.Serialize(props)
				: "{}",
		};

	static LogEntryRecord ToRecord(LogEntryCandidate c) =>
		LogEntryRecord.FromCandidate(c, LogEntryRecord.ComputeTemplateHash(c.MessageTemplate));

	async Task<IReadOnlyList<string>> RunAsync(string kql)
	{
		var code = KustoCode.Parse(kql);
		var query = KqlTransformer.Apply(_logDb.LogEntries, code);
		var list = query.ToList();
		return list.Select(r => r.Message).ToList();
	}

	[Fact]
	public async Task WhereLevel_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Level == 4");
		messages.Should().BeEquivalentTo(["boom", "crash on Earth"]);
	}

	[Fact]
	public async Task WhereLevel_OrderingTranslatesToSql()
	{
		var messages = await RunAsync("events | where Level >= 3");
		messages.Should().BeEquivalentTo(["boom", "meh", "crash on Earth"]);
	}

	[Fact]
	public async Task WhereServiceKey_Equality()
	{
		var messages = await RunAsync("events | where ServiceKey == 'svc-a'");
		messages.Should().BeEquivalentTo(["hello world", "meh"]);
	}

	[Fact]
	public async Task Take_LimitsRowsAgainstSqlite()
	{
		var messages = await RunAsync("events | take 2");
		messages.Should().HaveCount(2);
	}

	[Fact]
	public async Task OrderBy_TranslatesToSql()
	{
		var messages = await RunAsync("events | order by Id asc");
		messages.Should().ContainInOrder("hello world", "boom", "meh", "crash on Earth", "starting");
	}

	[Fact]
	public async Task AndCombinator_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Level == 4 and ServiceKey == 'svc-b'");
		messages.Should().BeEquivalentTo(["boom"]);
	}

	[Fact]
	public async Task MessageContains_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Message contains 'boom'");
		messages.Should().Contain("boom");
	}

	[Fact]
	public async Task TimestampGte_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Timestamp >= datetime(2026-04-19T10:03:00Z)");
		messages.Should().BeEquivalentTo(["meh", "crash on Earth", "starting"]);
	}

	[Fact]
	public async Task TimestampLt_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Timestamp < datetime(2026-04-19T10:03:00Z)");
		messages.Should().BeEquivalentTo(["hello world", "boom"]);
	}

	[Fact]
	public async Task TimestampRange_TranslatesToSql()
	{
		var messages = await RunAsync(
			"events | where Timestamp >= datetime(2026-04-19T10:02:00Z) and Timestamp < datetime(2026-04-19T10:05:00Z)");
		messages.Should().BeEquivalentTo(["boom", "meh", "crash on Earth"]);
	}

	[Fact]
	public async Task WhereIn_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Level in (3, 4)");
		messages.Should().BeEquivalentTo(["boom", "meh", "crash on Earth"]);
	}

	[Fact]
	public async Task WhereNotIn_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Level !in (4)");
		messages.Should().BeEquivalentTo(["hello world", "meh", "starting"]);
	}

	[Fact]
	public async Task WhereBetween_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Level between (3 .. 4)");
		messages.Should().BeEquivalentTo(["boom", "meh", "crash on Earth"]);
	}

	[Fact]
	public async Task WhereArithmetic_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Level + 1 == 5");
		messages.Should().BeEquivalentTo(["boom", "crash on Earth"]);
	}

	[Fact]
	public async Task WhereModulo_TranslatesToSql()
	{
		var messages = await RunAsync("events | where Id % 2 == 0");
		messages.Should().HaveCount(2);
	}

	[Fact]
	public async Task WhereIff_TranslatesToSql()
	{
		var messages = await RunAsync("events | where iff(Level >= 4, 1, 0) == 1");
		messages.Should().BeEquivalentTo(["boom", "crash on Earth"]);
	}

	[Fact]
	public async Task PropertiesEq_TranslatesToJsonExtract()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(30, LogLevel.Information, "login", "svc-a",
				JsonDocument.Parse("""{"user":"alice","region":"eu"}""").RootElement.EnumerateObject()
					.Aggregate(ImmutableDictionary<string, JsonElement>.Empty,
						(acc, p) => acc.Add(p.Name, p.Value.Clone())))),
			ToRecord(Mk(31, LogLevel.Information, "login", "svc-a",
				JsonDocument.Parse("""{"user":"bob","region":"us"}""").RootElement.EnumerateObject()
					.Aggregate(ImmutableDictionary<string, JsonElement>.Empty,
						(acc, p) => acc.Add(p.Name, p.Value.Clone())))),
		]);

		var messages = await RunAsync("events | where Properties.user == 'alice'");
		messages.Should().BeEquivalentTo(["login"]);
	}

	[Fact]
	public async Task SummarizeByProperty_GroupsViaJsonExtract()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(55, LogLevel.Information, "a", "svc-x",
				JsonDocument.Parse("""{"SourceContext":"Web"}""").RootElement.EnumerateObject()
					.Aggregate(ImmutableDictionary<string, JsonElement>.Empty,
						(acc, p) => acc.Add(p.Name, p.Value.Clone())))),
			ToRecord(Mk(56, LogLevel.Information, "b", "svc-x",
				JsonDocument.Parse("""{"SourceContext":"Web"}""").RootElement.EnumerateObject()
					.Aggregate(ImmutableDictionary<string, JsonElement>.Empty,
						(acc, p) => acc.Add(p.Name, p.Value.Clone())))),
			ToRecord(Mk(57, LogLevel.Information, "c", "svc-x",
				JsonDocument.Parse("""{"SourceContext":"Worker"}""").RootElement.EnumerateObject()
					.Aggregate(ImmutableDictionary<string, JsonElement>.Empty,
						(acc, p) => acc.Add(p.Name, p.Value.Clone())))),
		]);

		var code = KustoCode.Parse("events | summarize count() by Properties.SourceContext");
		var result = KqlTransformer.Execute(_logDb.LogEntries, code);

		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);

		result.Columns.Select(c => c.Name).Should().ContainInOrder("Properties.SourceContext", "count_");
		var byCtx = rows
			.Where(r => r[0] is string)
			.ToDictionary(r => (string)r[0]!, r => (long)r[1]!);
		byCtx["Web"].Should().Be(2);
		byCtx["Worker"].Should().Be(1);
	}
}
