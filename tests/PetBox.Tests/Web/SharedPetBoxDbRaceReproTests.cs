using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Web;

// REPRO of the primitive under the prod bug: PetBoxDb is a LinqToDB DataConnection (registered
// AddScoped, Program.cs:101) — ONE instance per HTTP request — and it is NOT thread-safe. The
// cross-scope search fan-out (CrossScopeTaskSearchService, up to 6 branches in parallel within
// ONE request scope) drives TaskBoardStore.FindAsync (TaskBoardStore.cs:96) on that shared
// instance from several threads at once.
public sealed class SharedPetBoxDbRaceReproTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;

	public SharedPetBoxDbRaceReproTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-dbrace-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		for (var i = 0; i < 8; i++)
			_db.Insert(new Project { Key = $"proj-{i}", WorkspaceKey = "ws1", Name = $"P{i}", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task ConcurrentFindAsync_OnOneScopedPetBoxDb_MustNotThrow()
	{
		for (var i = 0; i < 8; i++)
			await _store.CreateAsync($"proj-{i}", "intake", null, "intake");

		// Exactly the shape the fan-out produces: N parallel FindAsync(projectKey, board) on the
		// SAME PetBoxDb. Expected (before fix): InvalidOperationException
		// "Must add values for the following parameters: @projectKey, @board".
		var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
		for (var attempt = 0; attempt < 100; attempt++)
		{
			await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
			{
				try { await _store.FindAsync($"proj-{i % 8}", "intake"); }
				catch (Exception ex) { seen.Add(ex.GetType().Name + ": " + ex.Message); }
			})));
		}
		seen.Distinct().Should().BeEmpty();
	}
}
