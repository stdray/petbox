using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data.Migrations;

namespace PetBox.Tests.Migrations;

// A core.db parked at schema v49 — the state the LIVE database is in before M050 — built ONCE per
// process and handed out as file copies.
//
// Same trick, and for the same reason, as TestSchema's per-tier templates: xUnit news a test class
// per [Fact], so a plain per-test build re-runs forty-nine migrations for every test that wants the
// pre-M050 shape. Four such builds in a parallel suite is enough extra load to time out an
// unrelated fixture's MCP handshake — observed, not theorised. This is not routed through
// TestSchema because TestSchema's core template is FULLY migrated: a v50 file cannot be used to
// test what M050 does to a v49 one.
public static class PreM050CoreDb
{
	const long PreM050Version = 49;

	static readonly Lazy<string> Template =
		new(BuildTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

	// Materializes a v49 core.db at `path` and returns its connection string.
	public static string CopyTo(string path)
	{
		File.Copy(Template.Value, path);
		return $"Data Source={path}";
	}

	static string BuildTemplate()
	{
		var path = Path.Combine(Path.GetTempPath(), "petbox-tmpl-core-v49-" + Guid.NewGuid().ToString("N") + ".db");
		var cs = $"Data Source={path}";

		using (var services = new ServiceCollection()
			.AddFluentMigratorCore()
			.ConfigureRunner(rb => rb.AddSQLite().WithGlobalConnectionString(cs)
				.ScanIn(typeof(M001_Initial).Assembly).For.Migrations())
			.BuildServiceProvider())
		{
			using var scope = services.CreateScope();
			scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(PreM050Version);
		}

		// Fold the WAL back in and release the handle, so the copied snapshot is complete and
		// unlocked — the template is useless if a copy carries a half-written page.
		using (var conn = new SqliteConnection(cs))
		{
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
			cmd.ExecuteNonQuery();
		}
		SqliteConnection.ClearPool(new SqliteConnection(cs));
		return path;
	}
}
