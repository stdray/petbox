using Microsoft.Data.Sqlite;

namespace PetBox.Tests.Data.Schema;

// The config and log tiers are the two that got migrations LAST: until now their files were created
// by hand-written runtime DDL (ConfigSchema's `CREATE TABLE IF NOT EXISTS` + `AddColumnIfMissing`
// ladder, LogSchema's raw `CREATE TABLE IF NOT EXISTS`), with no VersionInfo and no drift detection.
// Their M001 is therefore a BASELINE THAT ADOPTS: every object is created only if absent.
//
// The golden snapshots pin what the migrations build on an EMPTY file. These tests pin the other
// half — that running the baseline over a file the OLD code left behind (a) does not fail, (b) does
// not touch the rows, and (c) converges on exactly the fresh shape. The legacy DDL below is copied
// verbatim from the deleted bootstrap code, including its historical intermediate stages: a live
// file may have stopped at ANY rung of the ladder (created before a column/table existed and never
// reopened by a newer build), and the baseline must accept all of them.
public sealed class LegacySchemaAdoptionTests : IDisposable
{
	readonly string _dir;

	public LegacySchemaAdoptionTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-legacy-adopt-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() => TestDirs.CleanupOrDefer(_dir);

	// ── config ─────────────────────────────────────────────────────────────────

	// The config schema as ConfigSchema.Ensure wrote it, at each stage it ever existed in:
	//   "oldest"  – before the secret/versioning/soft-delete work: no Kind/Ciphertext/Iv/AuthTag,
	//               no Version/ContentHash, no IsDeleted/DeletedAt, no IX_ConfigBindings_IsDeleted.
	//   "current" – the final shape of the hand DDL (what all 10 live workspace files carry).
	// Both must end up as the fresh, migrated shape — the "oldest" one via the ALTER ladder.
	[Theory]
	[InlineData("oldest")]
	[InlineData("current")]
	public void Config_baseline_adopts_a_legacy_file(string stage)
	{
		var db = Path.Combine(_dir, $"config-{stage}.db");
		var cs = $"Data Source={db}";
		Exec(cs, stage == "oldest" ? LegacyConfigOldest : LegacyConfigCurrent);

		// A binding with an ENCRYPTED secret in the "current" stage — the row shape whose loss is
		// unrecoverable — and a plain one in both.
		Exec(cs, """
			INSERT INTO ConfigBindings (Path, Value, Tags, CreatedAt, UpdatedAt)
			VALUES ('a/b', 'plain', '[]', '2026-01-01', '2026-01-01');
			""");
		if (stage == "current")
			Exec(cs, """
				INSERT INTO ConfigBindings (Path, Value, Tags, Kind, Ciphertext, Iv, AuthTag, CreatedAt, UpdatedAt)
				VALUES ('a/secret', '', '[]', 1, 'CIPHER', 'IV', 'TAG', '2026-01-01', '2026-01-01');
				""");

		PetBox.Config.Data.ConfigSchema.Ensure(cs);

		// Rows survived, secret intact.
		Scalar(cs, "SELECT COUNT(*) FROM ConfigBindings").Should().Be(stage == "current" ? 2L : 1L);
		if (stage == "current")
			Scalar(cs, "SELECT Ciphertext || '|' || Iv || '|' || AuthTag FROM ConfigBindings WHERE Path='a/secret'")
				.Should().Be("CIPHER|IV|TAG");

		// And the file now has the same shape a fresh one gets. The only expected difference is the
		// companion unique index FluentMigrator's `.Unique()` adds on a FRESH TagVocabulary (the
		// legacy table already enforces it inline, via sqlite_autoindex) — see M001_ConfigBaseline.
		AdoptedMatchesFresh("config", cs, allowedMissing: ["INDEX IX_TagVocabulary_TagKey UNIQUE origin=c (TagKey)"]);
	}

	// ── log ────────────────────────────────────────────────────────────────────

	// The log schema as LogSchema.Ensure wrote it:
	//   "entries-only" – the oldest shape: LogEntries + its 3 indexes, no Spans, no MetricPoints.
	//   "no-metrics"   – LogEntries + Spans, no MetricPoints (this is logs/$system.db on prod today).
	//   "current"      – all three tables.
	[Theory]
	[InlineData("entries-only")]
	[InlineData("no-metrics")]
	[InlineData("current")]
	public void Log_baseline_adopts_a_legacy_file(string stage)
	{
		var db = Path.Combine(_dir, $"log-{stage}.db");
		var cs = $"Data Source={db}";
		Exec(cs, LegacyLogEntries);
		if (stage != "entries-only") Exec(cs, LegacyLogSpans);
		if (stage == "current") Exec(cs, LegacyLogMetrics);

		Exec(cs, """
			INSERT INTO LogEntries (ServiceKey, TimestampMs, Level, Message, MessageTemplate)
			VALUES ('svc', 1, 2, 'hello', 'hello');
			""");

		PetBox.Log.Core.Data.LogSchema.Ensure(cs);

		Scalar(cs, "SELECT COUNT(*) FROM LogEntries").Should().Be(1L);
		Scalar(cs, "SELECT Message FROM LogEntries").Should().Be("hello");
		AdoptedMatchesFresh("log", cs);
	}

	// ── plumbing ───────────────────────────────────────────────────────────────

	// Snapshots the adopted file and a FRESH file of the same tier and demands they agree, line for
	// line, except for the explicitly allowed (and explained) leftovers.
	void AdoptedMatchesFresh(string tier, string adoptedCs, IReadOnlyList<string>? allowedMissing = null)
	{
		var freshCs = $"Data Source={Path.Combine(_dir, $"fresh-{tier}-{Guid.NewGuid():N}.db")}";
		if (tier == "config") PetBox.Config.Data.ConfigSchema.Ensure(freshCs);
		else PetBox.Log.Core.Data.LogSchema.Ensure(freshCs);

		var fresh = SchemaSnapshot.Capture(freshCs).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
		var adopted = SchemaSnapshot.Capture(adoptedCs).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);

		var missing = fresh.Where(l => !adopted.Contains(l)).ToList();
		missing.Should().BeEquivalentTo(allowedMissing ?? [],
			$"an adopted {tier} file must end up with the same schema a fresh one gets");

		var extra = adopted.Where(l => !fresh.Contains(l, StringComparer.Ordinal)).ToList();
		extra.Should().BeEmpty($"an adopted {tier} file must carry nothing a fresh one does not");
	}

	static void Exec(string cs, string sql)
	{
		using var conn = new SqliteConnection(cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		cmd.ExecuteNonQuery();
	}

	static object Scalar(string cs, string sql)
	{
		using var conn = new SqliteConnection(cs);
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = sql;
		return cmd.ExecuteScalar()!;
	}

	// ── the legacy DDL, verbatim from the deleted hand-written bootstraps ───────

	const string LegacyConfigOldest = """
		CREATE TABLE ConfigBindings (
			Id INTEGER PRIMARY KEY AUTOINCREMENT,
			Path TEXT NOT NULL,
			Value TEXT NOT NULL,
			Tags TEXT NOT NULL,
			CreatedAt TEXT NOT NULL,
			UpdatedAt TEXT NOT NULL
		);
		CREATE INDEX IX_ConfigBindings_Path ON ConfigBindings (Path);
		CREATE TABLE ConfigBindingHistory (
			Id INTEGER PRIMARY KEY AUTOINCREMENT,
			BindingId INTEGER NOT NULL,
			Action TEXT NOT NULL,
			Path TEXT NOT NULL,
			Tags TEXT NOT NULL,
			Kind INTEGER NOT NULL DEFAULT 0,
			OldValue TEXT,
			NewValue TEXT,
			Actor TEXT NOT NULL DEFAULT 'system',
			At TEXT NOT NULL
		);
		CREATE INDEX IX_ConfigBindingHistory_At ON ConfigBindingHistory (At DESC);
		CREATE INDEX IX_ConfigBindingHistory_Path ON ConfigBindingHistory (Path);
		CREATE TABLE TagVocabulary (
			Id INTEGER PRIMARY KEY AUTOINCREMENT,
			TagKey TEXT NOT NULL UNIQUE,
			Description TEXT,
			CreatedAt TEXT NOT NULL
		);
		""";

	const string LegacyConfigCurrent = """
		CREATE TABLE ConfigBindings (
			Id INTEGER PRIMARY KEY AUTOINCREMENT,
			Path TEXT NOT NULL,
			Value TEXT NOT NULL,
			Tags TEXT NOT NULL,
			Kind INTEGER NOT NULL DEFAULT 0,
			Ciphertext TEXT,
			Iv TEXT,
			AuthTag TEXT,
			Version INTEGER NOT NULL DEFAULT 1,
			ContentHash TEXT NOT NULL DEFAULT '',
			IsDeleted INTEGER NOT NULL DEFAULT 0,
			DeletedAt TEXT,
			CreatedAt TEXT NOT NULL,
			UpdatedAt TEXT NOT NULL
		);
		CREATE INDEX IX_ConfigBindings_Path ON ConfigBindings (Path);
		CREATE INDEX IX_ConfigBindings_IsDeleted ON ConfigBindings (IsDeleted);
		CREATE TABLE ConfigBindingHistory (
			Id INTEGER PRIMARY KEY AUTOINCREMENT,
			BindingId INTEGER NOT NULL,
			Action TEXT NOT NULL,
			Path TEXT NOT NULL,
			Tags TEXT NOT NULL,
			Kind INTEGER NOT NULL DEFAULT 0,
			OldValue TEXT,
			NewValue TEXT,
			Actor TEXT NOT NULL DEFAULT 'system',
			At TEXT NOT NULL
		);
		CREATE INDEX IX_ConfigBindingHistory_At ON ConfigBindingHistory (At DESC);
		CREATE INDEX IX_ConfigBindingHistory_Path ON ConfigBindingHistory (Path);
		CREATE TABLE TagVocabulary (
			Id INTEGER PRIMARY KEY AUTOINCREMENT,
			TagKey TEXT NOT NULL UNIQUE,
			Description TEXT,
			CreatedAt TEXT NOT NULL
		);
		""";

	const string LegacyLogEntries = """
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
		CREATE INDEX IX_LogEntries_ServiceKey_TimestampMs ON LogEntries (ServiceKey, TimestampMs DESC);
		CREATE INDEX IX_LogEntries_TimestampMs ON LogEntries (TimestampMs DESC);
		CREATE INDEX IX_LogEntries_Level ON LogEntries (Level);
		""";

	const string LegacyLogSpans = """
		CREATE TABLE Spans (
			SpanId            TEXT    PRIMARY KEY,
			TraceId           TEXT    NOT NULL,
			ParentSpanId      TEXT,
			Name              TEXT    NOT NULL,
			Kind              INTEGER NOT NULL,
			StartUnixNs       INTEGER NOT NULL,
			EndUnixNs         INTEGER NOT NULL,
			StatusCode        INTEGER NOT NULL,
			StatusDescription TEXT,
			AttributesJson    TEXT    NOT NULL DEFAULT '{}',
			EventsJson        TEXT    NOT NULL DEFAULT '[]',
			LinksJson         TEXT    NOT NULL DEFAULT '[]'
		);
		CREATE INDEX ix_spans_trace_start ON Spans(TraceId, StartUnixNs);
		CREATE INDEX ix_spans_start ON Spans(StartUnixNs);
		""";

	const string LegacyLogMetrics = """
		CREATE TABLE MetricPoints (
			Id                     INTEGER PRIMARY KEY AUTOINCREMENT,
			MetricName             TEXT    NOT NULL,
			MetricType             INTEGER NOT NULL,
			Unit                   TEXT,
			Description            TEXT,
			TimeUnixNs             INTEGER NOT NULL,
			StartUnixNs            INTEGER,
			Flags                  INTEGER,
			ValueDouble            REAL,
			ValueLong              INTEGER,
			AggregationTemporality INTEGER,
			IsMonotonic            INTEGER,
			Count                  INTEGER,
			Sum                    REAL,
			Min                    REAL,
			Max                    REAL,
			Scale                  INTEGER,
			ZeroCount              INTEGER,
			AttributesJson         TEXT    NOT NULL DEFAULT '{}',
			ExplicitBoundsJson     TEXT,
			BucketCountsJson       TEXT,
			PositiveBucketsJson    TEXT,
			NegativeBucketsJson    TEXT,
			QuantileValuesJson     TEXT,
			ExemplarsJson          TEXT
		);
		CREATE INDEX ix_metricpoints_name_time ON MetricPoints(MetricName, TimeUnixNs);
		CREATE INDEX ix_metricpoints_time ON MetricPoints(TimeUnixNs);
		""";
}
