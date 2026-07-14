using LinqToDB;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Tests.Web;

// The board page's tag-groups projection (board-tag-grouping): ?view=tags&by=<ordered ns list>
// flattens GetGroupedAsync into header/card rows. The projection is a pure view — the part_of
// tree is untouched (tag-grouping-is-projection).
//
// board-tag-grouping-disabled (owner call 2026-07-14): the mode is now DISABLED end to end —
// BoardViewModeRegistry.Resolve refuses to land ANY request on "tags" (see that file), so
// TaskBoardModel.LoadAsync's tags branch (`ResolvedViewMode == Tags && dims.Length > 0`) is now
// UNREACHABLE from a real request — ResolvedViewMode can no longer BE "tags", valid `by` or not.
// The tests below that used to assert a working grouping render now assert the opposite: even a
// well-formed `by` falls back to the tree, exactly like the pre-existing bad/empty-`by` case.
// GetGroupedAsync/FlattenGroups/_BoardViewTags.cshtml are all still wired and correct — they are
// simply never reached this way anymore (the code stays for a future re-enable, per the card).
public sealed class TaskBoardTagViewTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public TaskBoardTagViewTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-tagview-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(_store, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	TaskBoardModel Model() =>
		new(Flags(), _tasks, new CommentService(_factory), new NullSettingsResolver()) { WorkspaceKey = "ws", ProjectKey = "proj", Board = "g" };

	// a is in two areas (multimembership) + a concern; b is one area, no concern → "(none)".
	async Task Seed()
	{
		await _store.CreateAsync("proj", "g", null, "simple");
		await _tasks.UpsertAsync("proj", "g",
		[
			new NodePatch { Key = "a", Title = "A", Body = "x", Tags = ["area:ui", "area:llm", "concern:security"] },
			new NodePatch { Key = "b", Title = "B", Body = "x", Tags = ["area:ui"] },
		]);
	}

	[Fact]
	public async Task TagView_OrderedMultiKey_DisabledFallsBackToTree()
	{
		await Seed();
		var m = Model();
		m.ViewMode = "tags";
		m.By = "area, concern";
		await m.OnGetAsync(default);

		// board-tag-grouping-disabled: a well-formed multi-dimension `by` no longer matters — the
		// mode itself is unselectable, so this degrades exactly like an invalid one.
		m.IsTagView.Should().BeFalse();
		m.GroupRows.Should().BeEmpty();
		m.Nodes.Select(n => n.Key).Should().BeEquivalentTo(["a", "b"]); // the part_of tree still renders
	}

	[Fact]
	public async Task TagView_SingleKey_DisabledFallsBackToTree()
	{
		await Seed();
		var m = Model();
		m.ViewMode = "tags";
		m.By = "area";
		await m.OnGetAsync(default);

		m.IsTagView.Should().BeFalse();
		m.GroupRows.Should().BeEmpty();
		m.Nodes.Select(n => n.Key).Should().BeEquivalentTo(["a", "b"]);
	}

	[Fact]
	public async Task BadGroupBy_FallsBackToTree()
	{
		await Seed();
		foreach (var by in new[] { "status", "" })
		{
			var m = Model();
			m.ViewMode = "tags";
			m.By = by;
			await m.OnGetAsync(default);
			m.IsTagView.Should().BeFalse();   // invalid/empty namespace → stay on the tree
			m.GroupRows.Should().BeEmpty();
			m.Nodes.Should().NotBeEmpty();    // the part_of tree is still populated
		}
	}

	[Fact]
	public async Task DefaultView_IsTree()
	{
		await Seed();
		var m = Model();
		await m.OnGetAsync(default);
		m.IsTagView.Should().BeFalse();
		m.Nodes.Select(n => n.Key).Should().BeEquivalentTo(["a", "b"]);
	}
}
