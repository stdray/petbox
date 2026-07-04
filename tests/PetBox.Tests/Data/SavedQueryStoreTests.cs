using LinqToDB;
using LinqToDB.Async;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Data;

// Guards the "Save as" KQL query flow on the logs page (work card ui-saved-query-500):
// the SavedQueries table must be materialized by the Core migration set and a saved
// query must round-trip through PetBoxDb. A missing table here is exactly the prod 500.
public sealed class SavedQueryStoreTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;

	public SavedQueryStoreTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-savedquery-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public void Migration_Creates_SavedQueries_Table_And_Index()
	{
		var cs = _db.ConnectionString!;
		using var conn = new SqliteConnection(cs);
		conn.Open();

		using var tableCmd = conn.CreateCommand();
		tableCmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='SavedQueries'";
		((long)tableCmd.ExecuteScalar()!).Should().Be(1, "the M006 migration must create the SavedQueries table");

		using var idxCmd = conn.CreateCommand();
		idxCmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='IX_SavedQueries_ProjectKey'";
		((long)idxCmd.ExecuteScalar()!).Should().Be(1, "the project-scope lookup index must exist");
	}

	[Fact]
	public async Task SaveQuery_Insert_RoundTrips()
	{
		var now = DateTime.UtcNow;
		await _db.InsertAsync(new SavedQuery
		{
			Name = "errors last hour",
			Kql = "events | where Level == \"Error\"",
			ProjectKey = "kpvotes",
			CreatedAt = now,
			UpdatedAt = now,
		});

		var read = await _db.SavedQueries
			.Where(q => q.ProjectKey == "kpvotes")
			.OrderBy(q => q.Name)
			.ToListAsync();

		read.Should().ContainSingle();
		read[0].Name.Should().Be("errors last hour");
		read[0].Kql.Should().Be("events | where Level == \"Error\"");
		read[0].ProjectKey.Should().Be("kpvotes");
		read[0].Id.Should().BeGreaterThan(0, "Id is an identity column");
	}

	[Fact]
	public async Task SaveQuery_Is_Project_Scoped()
	{
		var now = DateTime.UtcNow;
		await _db.InsertAsync(new SavedQuery { Name = "q", Kql = "events", ProjectKey = "proj-a", CreatedAt = now, UpdatedAt = now });
		await _db.InsertAsync(new SavedQuery { Name = "q", Kql = "events", ProjectKey = "proj-b", CreatedAt = now, UpdatedAt = now });

		var a = await _db.SavedQueries.Where(q => q.ProjectKey == "proj-a").ToListAsync();
		a.Should().ContainSingle();
		a[0].ProjectKey.Should().Be("proj-a");
	}

	[Fact]
	public async Task SaveQuery_Update_Existing_Persists_NewKql()
	{
		var now = DateTime.UtcNow;
		await _db.InsertAsync(new SavedQuery { Name = "dupe", Kql = "events | take 1", ProjectKey = "proj", CreatedAt = now, UpdatedAt = now });

		var existing = (await _db.SavedQueries.Where(q => q.ProjectKey == "proj" && q.Name == "dupe").ToListAsync()).Single();
		await _db.UpdateAsync(existing with { Kql = "events | take 2", UpdatedAt = DateTime.UtcNow });

		var reread = (await _db.SavedQueries.Where(q => q.ProjectKey == "proj" && q.Name == "dupe").ToListAsync()).Single();
		reread.Kql.Should().Be("events | take 2");
	}
}
