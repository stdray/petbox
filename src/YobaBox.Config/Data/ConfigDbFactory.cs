using Microsoft.Data.Sqlite;

namespace YobaBox.Config.Data;

public interface IConfigDbFactory
{
	ConfigDb GetConfigDb(string workspaceKey);
	ValueTask DisposeAsync();
}

public sealed class ConfigDbFactory : IConfigDbFactory, IAsyncDisposable
{
	readonly string _baseDir;
	readonly Dictionary<string, ConfigDb> _dbs = [];
	readonly object _lock = new();

	public ConfigDbFactory(string baseDir)
	{
		_baseDir = baseDir;
		Directory.CreateDirectory(_baseDir);
	}

	public ConfigDb GetConfigDb(string workspaceKey)
	{
		lock (_lock)
		{
			if (_dbs.TryGetValue(workspaceKey, out var existing))
				return existing;

			var dbPath = Path.Combine(_baseDir, $"{workspaceKey}.db");
			var cs = $"Data Source={dbPath}";
			var db = new ConfigDb(ConfigDb.CreateOptions(cs));

			CreateSchema(cs);

			_dbs[workspaceKey] = db;
			return db;
		}
	}

	static void CreateSchema(string cs)
	{
		using var raw = new SqliteConnection(cs);
		raw.Open();
		using var cmd = raw.CreateCommand();
		cmd.CommandText = """
			CREATE TABLE IF NOT EXISTS ConfigBindings (
				Id INTEGER PRIMARY KEY AUTOINCREMENT,
				Path TEXT NOT NULL,
				Value TEXT NOT NULL,
				Tags TEXT NOT NULL,
				CreatedAt TEXT NOT NULL,
				UpdatedAt TEXT NOT NULL
			);
			CREATE INDEX IF NOT EXISTS IX_ConfigBindings_Path ON ConfigBindings (Path);
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
