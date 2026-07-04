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

	async Task<IReadOnlyList<string>> RunAtAsync(string kql, TimeProvider clock)
	{
		var code = KustoCode.Parse(kql);
		var query = KqlTransformer.Apply(_logDb.LogEntries, code, clock);
		return query.ToList().Select(r => r.Message).ToList();
	}

	sealed class FixedClock(DateTime utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
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

	// --- string ops over real SQLite: substr-based startswith/endswith, lower(), and the
	// registered scalar functions kql_has / kql_matches_regex / kql_extract ---

	[Fact]
	public async Task StartsWith_TranslatesToSql()
	{
		(await RunAsync("events | where Message startswith 'crash'")).Should().BeEquivalentTo(["crash on Earth"]);
		(await RunAsync("events | where Message startswith 'CRASH'")).Should().BeEquivalentTo(["crash on Earth"]); // ci
		(await RunAsync("events | where Message startswith_cs 'CRASH'")).Should().BeEmpty();
	}

	[Fact]
	public async Task EndsWith_TranslatesToSql()
	{
		(await RunAsync("events | where Message endswith 'world'")).Should().BeEquivalentTo(["hello world"]);
		(await RunAsync("events | where Message endswith 'EARTH'")).Should().BeEquivalentTo(["crash on Earth"]); // ci
	}

	[Fact]
	public async Task HasTerm_TranslatesToSqlViaRegisteredFunction()
	{
		(await RunAsync("events | where Message has 'earth'")).Should().BeEquivalentTo(["crash on Earth"]);
		(await RunAsync("events | where Message has 'world'")).Should().BeEquivalentTo(["hello world"]);
		// term match, not substring: 'art' is inside "starting" but not a whole term.
		(await RunAsync("events | where Message has 'art'")).Should().BeEmpty();
	}

	[Fact]
	public async Task MatchesRegex_TranslatesToSqlViaRegisteredFunction()
	{
		(await RunAsync("events | where Message matches regex '^h'")).Should().BeEquivalentTo(["hello world"]);
		(await RunAsync("events | where Message matches regex 'o.m'")).Should().BeEquivalentTo(["boom"]);
	}

	[Fact]
	public async Task StringFunctionsInWhere_TranslateToSql()
	{
		(await RunAsync("events | where tolower(Message) == 'boom'")).Should().BeEquivalentTo(["boom"]);
		(await RunAsync("events | where substring(Message, 0, 5) == 'hello'")).Should().BeEquivalentTo(["hello world"]);
		(await RunAsync("events | where extract('([a-z]+)', 1, Message) == 'boom'")).Should().BeEquivalentTo(["boom"]);
	}

	// --- datetime ops over real SQLite: ago() as an epoch-ms constant, startof* via strftime, and
	// datetime_diff via epoch-ms integer arithmetic ---

	[Fact]
	public async Task Ago_TranslatesToEpochConstantInSql()
	{
		// pinned now = 2026-04-19T10:04:30Z; seed timestamps are 10:01..10:05.
		var clock = new FixedClock(new DateTime(2026, 4, 19, 10, 4, 30, DateTimeKind.Utc));
		(await RunAtAsync("events | where Timestamp > ago(2m)", clock))
			.Should().BeEquivalentTo(["meh", "crash on Earth", "starting"]);
	}

	[Fact]
	public async Task StartOf_TranslatesToStrftimeInSql()
	{
		(await RunAsync("events | where startofday(Timestamp) == datetime(2026-04-19)")).Should().HaveCount(5);
		(await RunAsync("events | where startofmonth(Timestamp) == datetime(2026-04-01)")).Should().HaveCount(5);
	}

	[Fact]
	public async Task DateTimeDiff_TranslatesToSqlArithmetic()
	{
		// 10:01 is 601 minutes into the day; only the first seed row.
		(await RunAsync("events | where datetime_diff('minute', Timestamp, startofday(Timestamp)) == 601"))
			.Should().BeEquivalentTo(["hello world"]);
		(await RunAsync("events | where datetime_diff('hour', Timestamp, startofday(Timestamp)) == 10"))
			.Should().HaveCount(5); // all seed rows are in the 10:00 hour
	}

	[Fact]
	public async Task Top_TranslatesToSqlOrderByLimit()
	{
		var desc = await RunAsync("events | top 2 by Id desc");
		desc.Should().ContainInOrder("starting", "crash on Earth");
		var asc = await RunAsync("events | top 2 by Id asc");
		asc.Should().ContainInOrder("hello world", "boom");
	}

	[Fact]
	public async Task WhereThenTop_ComposesSqlFilterAndLimit()
	{
		var messages = await RunAsync("events | where Level == 4 | top 1 by Id asc");
		messages.Should().ContainInOrder("boom");
	}

	[Fact]
	public async Task Distinct_StreamsFromSqlPreFilter()
	{
		// `where` (SQL) runs pre-split; `distinct` (shape-changing) de-dups in memory.
		var code = KustoCode.Parse("events | where Level == 4 | distinct ServiceKey");
		var result = KqlTransformer.Execute(_logDb.LogEntries, code);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("ServiceKey");

		var values = new List<string?>();
		await foreach (var row in result.Rows)
			values.Add(row[0] as string);
		values.Should().BeEquivalentTo(["svc-b", "svc-c"]); // the two Error rows
	}

	[Fact]
	public async Task Join_OverRealSqlite_BothSidesHitDb()
	{
		// Left prefix (where Level == 4) AND the right subquery (where Level >= 3) both run as SQLite
		// SQL against the same LogDb before the in-memory hash join. Seed: Error rows are Id2(svc-b) and
		// Id4(svc-c); right Level>=3 = Id2(svc-b),Id3(svc-a),Id4(svc-c). Join on ServiceKey pairs each
		// Error row to the same-service right row(s): (2,2) and (4,4).
		var code = KustoCode.Parse(
			"events | where Level == 4 | join kind=inner (events | where Level >= 3) on ServiceKey | project Id, Id1");
		var result = KqlTransformer.Execute(_logDb.LogEntries, code);
		result.Columns.Select(c => c.Name).Should().ContainInOrder("Id", "Id1");

		var rows = new List<(long, long)>();
		await foreach (var row in result.Rows)
			rows.Add(((long)row[0]!, (long)row[1]!));
		rows.Should().BeEquivalentTo([(2L, 2L), (4L, 4L)]);
	}

	[Fact]
	public async Task SummarizeSumByLevel_OverSqlSource()
	{
		var code = KustoCode.Parse("events | summarize Total = sum(Id) by Level");
		var result = KqlTransformer.Execute(_logDb.LogEntries, code);

		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);
		var byLevel = rows.ToDictionary(r => (int)r[0]!, r => (long)r[1]!);
		byLevel[(int)LogLevel.Error].Should().Be(2 + 4); // ids 2 and 4
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

	// --- typed Properties over real SQLite, ingested through the real CLEF parser: the bare-name
	// fallback (`where DeviceId == ...`) resolves as a Properties lookup and translates to json_extract,
	// and `toint(Properties.Status)` compares NUMERICALLY via the registered kql_tolong function (a
	// plain CAST would read '99' as bigger than '500' textually / '0' for garbage). ---

	[Fact]
	public async Task TypedProperties_BareFallbackAndNumericConversion_OverSqlite()
	{
		// Ingest through the actual CLEF parser so PropertiesJson has the production stored shape.
		var lines = new[]
		{
			"""{"@t":"2026-04-19T10:20:00Z","@m":"req a","DeviceId":"smoke-device","Status":200}""",
			"""{"@t":"2026-04-19T10:21:00Z","@m":"req b","DeviceId":"smoke-device","Status":500}""",
			"""{"@t":"2026-04-19T10:22:00Z","@m":"req c","DeviceId":"other-device","Status":503}""",
			"""{"@t":"2026-04-19T10:23:00Z","@m":"req d","DeviceId":"smoke-device","Status":"garbage"}""",
		};
		var records = lines
			.Select((l, i) => CleFParser.ParseLine(l, i + 1))
			.Select(r => { r.IsSuccess.Should().BeTrue(); return ToRecord(r.Event!); })
			.ToList();
		await _logDb.LogEntries.BulkCopyAsync(records);

		// bare-name fallback → json_extract string equality, translated to SQL.
		(await RunAsync("events | where DeviceId == 'smoke-device'"))
			.Should().BeEquivalentTo(["req a", "req b", "req d"]);

		// honest numeric comparison: only the two Status >= 500 rows; the garbage row is null → excluded.
		(await RunAsync("events | where toint(Properties.Status) >= 500"))
			.Should().BeEquivalentTo(["req b", "req c"]);

		// combined: bare fallback + numeric conversion, both in SQL.
		(await RunAsync("events | where DeviceId == 'smoke-device' and toint(Status) >= 500"))
			.Should().BeEquivalentTo(["req b"]);
	}

	static ImmutableDictionary<string, JsonElement> Props(string json) =>
		JsonDocument.Parse(json).RootElement.EnumerateObject()
			.Aggregate(ImmutableDictionary<string, JsonElement>.Empty, (acc, p) => acc.Add(p.Name, p.Value.Clone()));

	// --- F3 (review): != excludes null operands identically in the real SQLite path ---

	[Fact]
	public async Task NotEqual_OverNullConversion_ExcludesNullRows_InSql()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(40, LogLevel.Information, "s200", "svc-z", Props("""{"Status":200}"""))),
			ToRecord(Mk(41, LogLevel.Information, "s500", "svc-z", Props("""{"Status":500}"""))),
			ToRecord(Mk(42, LogLevel.Information, "sbad", "svc-z", Props("""{"Status":"x"}"""))), // null
		]);
		// 200 excluded (equal), 500 kept, the unparseable (null) row excluded — matching Kusto (and the
		// in-memory path), NOT C#'s lifted `!=` which would keep the null row.
		(await RunAsync("events | where ServiceKey == 'svc-z' and toint(Properties.Status) != 200"))
			.Should().BeEquivalentTo(["s500"]);
	}

	// --- F6 (review): a bare name resolves to a real column case-insensitively, in SQL ---

	[Fact]
	public async Task CaseInsensitiveColumnName_ResolvesToRealColumn_InSql()
	{
		(await RunAsync("events | where level == 4")).Should().BeEquivalentTo(["boom", "crash on Earth"]);
		(await RunAsync("events | where message == 'boom'")).Should().BeEquivalentTo(["boom"]);
	}

	// --- F7 (review): tobool over a JSON boolean agrees on SQL (integer 1/0) and in-memory (true/false) ---

	[Fact]
	public async Task ToBool_OverJsonBoolean_AgreesInSql()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(45, LogLevel.Information, "on", "svc-b", Props("""{"Enabled":true}"""))),
			ToRecord(Mk(46, LogLevel.Information, "off", "svc-b", Props("""{"Enabled":false}"""))),
		]);
		// SQLite json_extract yields INTEGER 1/0 for a JSON boolean; kql_tobool must accept it.
		(await RunAsync("events | where ServiceKey == 'svc-b' and tobool(Properties.Enabled) == true"))
			.Should().BeEquivalentTo(["on"]);
		(await RunAsync("events | where ServiceKey == 'svc-b' and tobool(Properties.Enabled) == false"))
			.Should().BeEquivalentTo(["off"]);
	}

	// --- F8 (review): negative substring start is clamped to 0 in SQL, matching the C# body ---

	[Fact]
	public async Task Substring_NegativeStart_ClampedInSql()
	{
		// substr with a negative start must clamp to 0 (KQL), NOT count from the end (SQLite default), so a
		// negative-start substring equals the same-length substring from 0 on every row.
		(await RunAsync("events | where substring(Message, -100, 5) == substring(Message, 0, 5)"))
			.Should().HaveCount(5);
		(await RunAsync("events | where substring(Message, -3) == Message")).Should().HaveCount(5);
	}

	// --- spans-review fix 1: LevelName is a computed CASE column, so a pre-split where/order over it
	// must TRANSLATE to SQLite (a raw LogLevelNames.ToName call used to die at enumeration here) ---

	[Fact]
	public async Task LevelName_Where_TranslatesToSql()
	{
		(await RunAsync("events | where LevelName == 'Error'"))
			.Should().BeEquivalentTo(["boom", "crash on Earth"]);
	}

	[Fact]
	public async Task LevelName_OrderBy_TranslatesToSql()
	{
		// Alphabetical by name: Debug(starting), Error(boom/crash), Information(hello world), Warning(meh).
		var messages = await RunAsync("events | order by LevelName asc");
		messages[0].Should().Be("starting");
		messages[^1].Should().Be("meh");
	}

	// --- spans-review fix 3: the special-column literal coercion must key on the RESOLVED canonical
	// name, so the case-insensitive `timestamp` form takes datetime literals like `Timestamp` does ---

	[Fact]
	public async Task LowercaseTimestamp_WithDatetimeLiteral_TranslatesToSql()
	{
		(await RunAsync("events | where timestamp >= datetime(2026-04-19T10:03:00Z)"))
			.Should().BeEquivalentTo(["meh", "crash on Earth", "starting"]);
	}

	// --- spans-review fix 4: datetime literals normalize inside `between` on the SQL path too ---

	[Fact]
	public async Task TimestampBetween_DatetimeLiterals_TranslatesToSql()
	{
		(await RunAsync(
			"events | where Timestamp between (datetime(2026-04-19T10:02:00Z) .. datetime(2026-04-19T10:04:00Z))"))
			.Should().BeEquivalentTo(["boom", "meh", "crash on Earth"]); // inclusive bounds
	}

	// --- spans-review fix 5: an out-of-int-range literal against an int column (Level) is a precise
	// user error, not a raw OverflowException ---

	[Fact]
	public async Task Level_OutOfRangeLiteral_ThrowsPrecise()
	{
		var act = () => RunAsync("events | where Level == 3000000000");
		await act.Should().ThrowAsync<UnsupportedKqlException>().WithMessage("*out of range*");
	}

	// --- Properties["dotted.key"] bracket indexing: a flat lookup via SQLite's BUILTIN json_extract
	// with a QUOTED-label path ('$."region.code"'), inside which dots are literal key characters —
	// the unquoted path syntax would wrongly split the key on dots ---

	[Fact]
	public async Task BracketIndexedDottedKey_Where_TranslatesToSql()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(50, LogLevel.Information, "req-eu", "svc-d", Props("""{"region.code":"eu","http.status_code":200}"""))),
			ToRecord(Mk(51, LogLevel.Information, "req-us", "svc-d", Props("""{"region.code":"us","http.status_code":500}"""))),
		]);

		(await RunAsync("""events | where ServiceKey == 'svc-d' and Properties["region.code"] == "eu" """))
			.Should().BeEquivalentTo(["req-eu"]);
		// typed conversion over the indexed value, still in SQL
		(await RunAsync("""events | where ServiceKey == 'svc-d' and toint(Properties["http.status_code"]) >= 500"""))
			.Should().BeEquivalentTo(["req-us"]);
	}

	[Fact]
	public async Task BracketIndexedValue_Project_OverSqlSource()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(52, LogLevel.Information, "proj-row", "svc-e", Props("""{"petbox.request_chars":250}"""))),
		]);

		var code = KustoCode.Parse("""events | where ServiceKey == 'svc-e' | project V = Properties["petbox.request_chars"]""");
		var result = KqlTransformer.Execute(_logDb.LogEntries, code);
		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);
		rows.Should().ContainSingle();
		rows[0][0].Should().Be("250"); // string representation of the JSON number, like bare access
	}

	[Fact]
	public async Task BracketIndexedKey_WithQuotesAndSpecialChars_NormalizedAtBothBoundaries()
	{
		// The raw key carries a double quote + dot + space. The WRITE boundary (PropertiesJsonSerializer,
		// which Mk routes through) normalizes it to `we_ird. key` before storage; the SEARCH boundary
		// applies the same KqlPropertyKeys rule to the requested key — so BOTH the raw spelling and the
		// normalized spelling find the row, and the stored key embeds safely in the quoted JSON-path label.
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(53, LogLevel.Information, "weird-row", "svc-f", Props("""{"we\"ird. key":"v1"}"""))),
		]);

		(await RunAsync("events | where ServiceKey == 'svc-f' and Properties[\"we\\\"ird. key\"] == 'v1'"))
			.Should().BeEquivalentTo(["weird-row"]); // raw request, normalized at search
		(await RunAsync("events | where ServiceKey == 'svc-f' and Properties[\"we_ird. key\"] == 'v1'"))
			.Should().BeEquivalentTo(["weird-row"]); // the normalized (stored) form directly
	}

	// --- value-representation parity (live-confirmed prod bug): a bag lookup compared to a string
	// literal must agree pre-split (SQL) and post-split (in-memory). Raw json_extract yields INTEGER
	// affinity for JSON numbers (SQLite `integer = 'text'` is silently FALSE) and 0/1 for booleans;
	// the CASE json_type / CAST AS TEXT shape renders exactly the in-memory text representation. ---

	async Task<IReadOnlyList<object?>> PostSplitMessages(string kql)
	{
		var result = KqlTransformer.Execute(_logDb.LogEntries, KustoCode.Parse(kql));
		var rows = new List<object?[]>();
		await foreach (var row in result.Rows) rows.Add(row);
		return rows.Select(r => r[0]).ToList();
	}

	[Fact]
	public async Task NumericBagValue_StringLiteral_AgreesPreAndPostSplit()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(20, LogLevel.Information, "st200", "svc-g", Props("""{"Status":200}"""))),
			ToRecord(Mk(21, LogLevel.Information, "st500", "svc-g", Props("""{"Status":500}"""))),
		]);

		// pre-split: the predicate runs as SQLite SQL (this returned 0 rows on prod before the fix)
		(await RunAsync("events | where ServiceKey == 'svc-g' and Properties.Status == '200'"))
			.Should().BeEquivalentTo(["st200"]);
		// post-split: the SAME predicate after a shape change runs in-memory — must agree
		(await PostSplitMessages(
			"events | where ServiceKey == 'svc-g' | extend one = 1 | where Properties.Status == '200' | project Message"))
			.Should().BeEquivalentTo(["st200"]);
	}

	[Fact]
	public async Task BooleanBagValue_StringLiteral_AgreesPreAndPostSplit()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(25, LogLevel.Information, "flag-on", "svc-h", Props("""{"Enabled":true}"""))),
			ToRecord(Mk(26, LogLevel.Information, "flag-off", "svc-h", Props("""{"Enabled":false}"""))),
		]);

		// json_type CASE renders 'true'/'false' — the in-memory GetRawText form, NOT SQLite's 0/1.
		(await RunAsync("events | where ServiceKey == 'svc-h' and Properties.Enabled == 'true'"))
			.Should().BeEquivalentTo(["flag-on"]);
		(await PostSplitMessages(
			"events | where ServiceKey == 'svc-h' | extend one = 1 | where Properties.Enabled == 'true' | project Message"))
			.Should().BeEquivalentTo(["flag-on"]);
	}

	// --- intake kql-propertiesjson-identity-split: `PropertiesJson` is the SAME real column pre- and
	// post-split. Pre-split it used to fall through the bare-name fallback as
	// Properties["PropertiesJson"] → json_extract NULL → a supported `where` silently returned 0 rows,
	// while post-split the streamed shape exposed it as a genuine column. ---

	[Fact]
	public async Task PropertiesJsonColumn_SameMeaningPreAndPostSplit()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(55, LogLevel.Information, "with-ctx", "svc-pj", Props("""{"SourceContext":"PetBox.Web.Mcp"}"""))),
			ToRecord(Mk(56, LogLevel.Information, "no-ctx", "svc-pj", Props("""{"Other":"x"}"""))),
		]);

		// pre-split: a real column filter over the raw JSON text, translated to SQL (was 0 rows).
		(await RunAsync("events | where ServiceKey == 'svc-pj' and PropertiesJson contains 'SourceContext'"))
			.Should().BeEquivalentTo(["with-ctx"]);
		// case-insensitive binding, like every other column.
		(await RunAsync("events | where ServiceKey == 'svc-pj' and propertiesjson contains 'SourceContext'"))
			.Should().BeEquivalentTo(["with-ctx"]);
		// the SAME predicate post-split must agree.
		(await PostSplitMessages(
			"events | where ServiceKey == 'svc-pj' | extend one = 1 | where PropertiesJson contains 'SourceContext' | project Message"))
			.Should().BeEquivalentTo(["with-ctx"]);
	}

	// --- ServerSideOnly=true: the predicate must appear IN the generated SQL. A client-side fallback
	// (ServerSideOnly=false) would materialize the whole table and the filter would vanish from the
	// SQL text — this pins that the bag lookup and the computed name column translate server-side. ---

	[Fact]
	public void GeneratedSql_BagLookupAndNameColumn_TranslateServerSide()
	{
		var bag = KqlTransformer.Apply(_logDb.LogEntries,
			KustoCode.Parse("""events | where Properties["region.code"] == "eu" """));
		var bagSql = bag.ToSqlQuery().Sql;
		bagSql.Should().Contain("json_extract");
		bagSql.Should().Contain("region.code"); // the quoted-label path carries the dotted key intact

		var levelName = KqlTransformer.Apply(_logDb.LogEntries,
			KustoCode.Parse("events | where LevelName == 'Error'"));
		levelName.ToSqlQuery().Sql.Should().Contain("CASE"); // the LevelName CASE mapping, in SQL
	}

	// --- review #1: JSON number TEXT forms agree across pipeline positions. SQLite's CAST
	// canonicalizes the spelling ('2.50' → '2.5', '1e3' → '1000.0'); the in-memory JsonGet renders
	// numbers the same way (NumberText). This test compares against ACTUAL SQLite output: the same
	// literal must match the row on the pre-split (SQL) path and the post-split (in-memory) path.
	// NOTE: extreme magnitudes are pinned here too ('1.0e+20'); should SQLite's %.15g and .NET's G15
	// ever disagree, it will be in the last rounding digits of such extremes — the accepted residual
	// edge documented at KqlSqlExpressions.NumberText. ---

	[Fact]
	public async Task NumberTextForms_AgreeAcrossPipelinePositions()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(35, LogLevel.Information, "nums", "svc-n",
				Props("""{"a":2.50,"b":1e3,"c":3,"d":2.5,"x":1e20}"""))),
		]);

		// the pinned canonical text forms (what JsonGet/NumberText renders in-memory)
		var expected = new Dictionary<string, string>
		{
			["a"] = "2.5",      // trailing zero dropped
			["b"] = "1000.0",   // e-notation source, integral real → '.0'
			["c"] = "3",        // integer stays plain
			["d"] = "2.5",      // canonical already
			["x"] = "1.0e+20",  // e-notation with '.0' mantissa
		};
		foreach (var (key, form) in expected)
		{
			// post-split: the in-memory rendering equals the pinned form
			(await PostSplitMessages(
				$"events | where ServiceKey == 'svc-n' | extend one = 1 | where Properties.{key} == '{form}' | project Message"))
				.Should().BeEquivalentTo(["nums"], $"post-split (in-memory) text form of key '{key}'");
			// pre-split: ACTUAL SQLite CAST renders the same text for the same row
			(await RunAsync($"events | where ServiceKey == 'svc-n' and Properties.{key} == '{form}'"))
				.Should().BeEquivalentTo(["nums"], $"pre-split (SQLite CAST) text form of key '{key}'");
		}
	}

	// --- review #4: string ordering must not depend on pipeline position. Pre-split ORDER BY runs
	// under SQLite's BINARY collation ('B' 0x42 < 'a' 0x61); the post-split comparer is ordinal. ---

	[Fact]
	public async Task StringSort_BinaryCollation_AgreesPreAndPostSplit()
	{
		await _logDb.LogEntries.BulkCopyAsync([
			ToRecord(Mk(33, LogLevel.Information, "row-apple", "svc-j", Props("""{"fruit":"apple"}"""))),
			ToRecord(Mk(34, LogLevel.Information, "row-Banana", "svc-j", Props("""{"fruit":"Banana"}"""))),
		]);

		var pre = await RunAsync("events | where ServiceKey == 'svc-j' | order by fruit asc");
		pre[0].Should().Be("row-Banana"); // BINARY: uppercase first

		var post = await PostSplitMessages(
			"events | where ServiceKey == 'svc-j' | extend one = 1 | order by fruit asc | project Message");
		post[0].Should().Be("row-Banana"); // ordinal comparer matches BINARY — same winner
	}
}
