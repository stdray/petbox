using LinqToDB;
using LinqToDB.Data;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

[Collection("DataModule")]
public sealed class TaskBoardStoreTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;

	public TaskBoardStoreTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-taskboard-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	[Fact]
	public async Task Create_InsertsMeta_AndMaterializesFile()
	{
		var meta = await _store.CreateAsync("proj", "roadmap", "the plan");
		meta.Name.Should().Be("roadmap");
		(await _store.ExistsAsync("proj", "roadmap")).Should().BeTrue();
		File.Exists(ScopedDbFiles.PathFor(_factory.BaseDir, "proj", "roadmap")).Should().BeTrue();
	}

	[Fact]
	public async Task List_ReturnsCreatedBoards_Ordered()
	{
		await _store.CreateAsync("proj", "beta", null);
		await _store.CreateAsync("proj", "alpha", null);
		(await _store.ListAsync("proj")).Select(b => b.Name).Should().Equal("alpha", "beta");
	}

	[Fact]
	public async Task Delete_RemovesMeta_AndFile()
	{
		await _store.CreateAsync("proj", "roadmap", null);
		(await _store.DeleteAsync("proj", "roadmap")).Should().BeTrue();
		(await _store.ExistsAsync("proj", "roadmap")).Should().BeFalse();
		File.Exists(ScopedDbFiles.PathFor(_factory.BaseDir, "proj", "roadmap")).Should().BeFalse();
	}

	[Fact]
	public async Task Create_Duplicate_Throws() =>
		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
		{
			await _store.CreateAsync("proj", "roadmap", null);
			await _store.CreateAsync("proj", "roadmap", null);
		});

	[Fact]
	public async Task Create_InvalidName_Throws() =>
		await Assert.ThrowsAsync<ArgumentException>(() => _store.CreateAsync("proj", "Bad Name", null));

	[Fact]
	public async Task Create_UnknownProject_Throws() =>
		await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateAsync("nope", "roadmap", null));

	[Fact]
	public async Task PlanNode_TemporalRoundtrip_ThroughBoardFile()
	{
		await _store.CreateAsync("proj", "roadmap", null);
		var ctx = _store.GetContext("proj", "roadmap");

		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new PlanNode { Key = "Phase 1", Version = 0, Status = PlanStatus.InProgress, Body = "Foundation", Priority = 100 },
		});
		r.Applied.Should().BeTrue();
		r.Inserted.Should().Be(1);

		var active = ctx.PlanNodes.Where(n => n.ActiveTo == null).ToList();
		active.Should().ContainSingle();
		active[0].Status.Should().Be(PlanStatus.InProgress);
		active[0].Body.Should().Be("Foundation");
	}
}
