using LinqToDB;
using PetBox.Config.Data;
using PetBox.Core.Models;

namespace PetBox.Tests.Config;

// Isolates the "create a binding" persistence sequence the Editor page runs:
// insert the binding (identity), then insert its history row (BindingId = newId).
// Regression for: SQLite NOT NULL on ConfigBindingHistory.BindingId.
public sealed class ConfigHistoryInsertTests
{
	[Fact]
	public async Task Create_Binding_Then_History_Persists_BindingId()
	{
		var dir = Path.Combine(Path.GetTempPath(), "petbox-cfg-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		var cs = $"Data Source={Path.Combine(dir, "cfg.db")}";
		ConfigSchema.Ensure(cs);

		using var db = new ConfigDb(ConfigDb.CreateOptions(cs));

		var newId = await db.InsertWithInt64IdentityAsync(new ConfigBinding
		{
			Path = "x.y",
			Tags = "ws:$system",
			Kind = BindingKind.Plain,
			Value = "1",
			Version = 1,
			ContentHash = "h",
			CreatedAt = DateTime.UtcNow,
			UpdatedAt = DateTime.UtcNow,
		});

		Assert.True(newId > 0, $"newId={newId}");

		// Was: 'INSERT INTO ConfigBindingHistory DEFAULT VALUES' because the record
		// had no [Column] attributes (this project maps columns by attribute) → the
		// NOT NULL BindingId column blew up.
		await db.InsertAsync(new ConfigBindingHistoryEntry
		{
			BindingId = newId,
			Action = "Create",
			Path = "x.y",
			Tags = "ws:$system",
			Kind = BindingKind.Plain,
			NewValue = "1",
			Actor = "t",
			At = DateTime.UtcNow,
		});

		var hist = db.History.Where(h => h.BindingId == newId).ToList();
		Assert.Single(hist);
		Assert.Equal(newId, hist[0].BindingId);
		Assert.Equal("Create", hist[0].Action);
	}
}
