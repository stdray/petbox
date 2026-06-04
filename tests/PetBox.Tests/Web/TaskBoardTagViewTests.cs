using LinqToDB;
using Microsoft.Data.Sqlite;
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
// tree is untouched (tag-grouping-is-projection); a bad/empty `by` falls back to the tree.
[Collection("DataModule")]
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
		MigrationRunner.Run(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
		_tasks = new TasksService(_store, new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	static FeatureFlags Flags() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{ ["Features:Tasks"] = "true" }).Build());

	TaskBoardModel Model() =>
		new(Flags(), _tasks, new CommentService(_factory)) { WorkspaceKey = "ws", ProjectKey = "proj", Board = "g" };

	// a is in two areas (multimembership) + a concern; b is one area, no concern → "(none)".
	async Task Seed()
	{
		await _store.CreateAsync("proj", "g", null, "free");
		await _tasks.UpsertAsync("proj", "g",
		[
			new NodePatch { Key = "a", Title = "A", Body = "x", Tags = ["area:ui", "area:llm", "concern:security"] },
			new NodePatch { Key = "b", Title = "B", Body = "x", Tags = ["area:ui"] },
		]);
	}

	[Fact]
	public async Task TagView_OrderedMultiKey_FlattensNestedHeadersAndCards()
	{
		await Seed();
		var m = Model();
		m.ViewMode = "tags";
		m.By = "area, concern";
		await m.OnGetAsync(default);

		m.IsTagView.Should().BeTrue();
		m.GroupDims.Should().Equal("area", "concern");

		// Top-level (depth 0) group headers are the area buckets — a is a multimember so it
		// shows under both. Headers carry no node, only a group key.
		m.GroupRows.Where(r => r.Node is null && r.Depth == 0).Select(r => r.GroupKey)
			.Should().Equal("area:llm", "area:ui"); // ordered by key, "(none)" would be last

		// Multimembership: a's card appears twice (once per area), b's once.
		m.GroupRows.Count(r => r.Node?.Key == "a").Should().Be(2);
		m.GroupRows.Count(r => r.Node?.Key == "b").Should().Be(1);

		// Inner nesting under area:ui: concern:security {a} then "(none)" {b}; cards sit one
		// level deeper than their group header.
		var ui = m.GroupRows.SkipWhile(r => r.GroupKey != "area:ui").Skip(1)
			.TakeWhile(r => !(r.Node is null && r.Depth == 0)).ToList();
		ui.Where(r => r.Node is null).Select(r => r.GroupKey).Should().Equal("concern:security", "(none)");
		ui.Single(r => r.Node?.Key == "a").Depth.Should().Be(2);
	}

	[Fact]
	public async Task TagView_SingleKey_LeafGroupsHoldCards()
	{
		await Seed();
		var m = Model();
		m.ViewMode = "tags";
		m.By = "area";
		await m.OnGetAsync(default);

		m.IsTagView.Should().BeTrue();
		m.GroupDims.Should().Equal("area");
		// Single dimension → each area header (depth 0) is followed directly by its node cards (depth 1).
		m.GroupRows.Where(r => r.Node is null).Select(r => r.GroupKey).Should().Equal("area:llm", "area:ui");
		m.GroupRows.Where(r => r.Node is not null).Should().OnlyContain(r => r.Depth == 1);
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
