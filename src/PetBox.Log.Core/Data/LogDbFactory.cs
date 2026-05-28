using LinqToDB;
using Microsoft.Data.Sqlite;

namespace PetBox.Log.Core.Data;

public interface ILogDbFactory
{
	LogDb GetLogDb(string projectKey);
	ValueTask DisposeAsync();
}

public sealed class LogDbFactory : ILogDbFactory, IAsyncDisposable
{
	readonly string _baseDir;
	readonly Dictionary<string, LogDb> _dbs = [];
	readonly object _lock = new();

	public LogDbFactory(string baseDir)
	{
		_baseDir = baseDir;
		Directory.CreateDirectory(_baseDir);
	}

	public LogDb GetLogDb(string projectKey)
	{
		lock (_lock)
		{
			if (_dbs.TryGetValue(projectKey, out var existing))
				return existing;

			var dbPath = Path.Combine(_baseDir, $"{projectKey}.db");
			var cs = $"Data Source={dbPath}";
			var db = new LogDb(LogDb.CreateOptions(cs));

			CreateSchema(db, cs);

			_dbs[projectKey] = db;
			return db;
		}
	}

	static void CreateSchema(LogDb db, string cs)
	{
		using var raw = new SqliteConnection(cs);
		raw.Open();
		using var cmd = raw.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS LogEntries (
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
			CREATE INDEX IF NOT EXISTS IX_LogEntries_ServiceKey_TimestampMs ON LogEntries (ServiceKey, TimestampMs DESC);
			CREATE INDEX IF NOT EXISTS IX_LogEntries_TimestampMs ON LogEntries (TimestampMs DESC);
			CREATE INDEX IF NOT EXISTS IX_LogEntries_Level ON LogEntries (Level);

			CREATE TABLE IF NOT EXISTS Spans (
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
			CREATE INDEX IF NOT EXISTS ix_spans_trace_start ON Spans(TraceId, StartUnixNs);
			CREATE INDEX IF NOT EXISTS ix_spans_start ON Spans(StartUnixNs);
			""";
		cmd.ExecuteNonQuery();
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var db in _dbs.Values)
			await db.DisposeAsync();
		_dbs.Clear();
	}
}
