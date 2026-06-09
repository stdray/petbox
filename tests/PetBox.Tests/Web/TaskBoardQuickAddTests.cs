using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Tests.Web;

// The board page's quick-add form is rejected only where a node needs a link at birth the
// bare form can't supply — Spec (ideaRef) and Work (specRef). Free/Ideas/Intake keep it.
// These tests verify the render flag + POST gate track the single WorkflowCatalog knob; the
// expectation is read from that same knob, so flipping a kind's policy flips both together.
[Collection("DataModule")]
public sealed class TaskBoardQuickAddTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public TaskBoardQuickAddTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-quickadd-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
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

	async Task<TaskBoardModel> Board(BoardKind kind)
	{
		var board = "b-" + kind.ToString().ToLowerInvariant();
		await _store.CreateAsync("proj", board, null, kind.ToString());
		return new TaskBoardModel(Flags(), _tasks, new CommentService(_factory)) { WorkspaceKey = "ws", ProjectKey = "proj", Board = board };
	}

	int ActiveNodeCount(string board) =>
		_store.GetContext("proj").PlanNodes.Count(n => n.Board == board && n.ActiveTo == null);

	// The render decision and the POST gate are BOTH driven off the single catalog knob —
	// the test reads the same source of truth, so flipping QuickAddAllowed for a kind flips
	// the expectation with it (no hardcoded per-kind policy to keep in sync).
	public static IEnumerable<object[]> AllKinds() =>
		Enum.GetValues<BoardKind>().Select(k => new object[] { k });

	[Theory]
	[MemberData(nameof(AllKinds))]
	public async Task OnGet_ShowsQuickAdd_PerCatalogPolicy(BoardKind kind)
	{
		var model = await Board(kind);
		await model.OnGetAsync(default);
		model.ShowQuickAdd.Should().Be(WorkflowCatalog.QuickAddAllowed(kind));
	}

	[Theory]
	[MemberData(nameof(AllKinds))]
	public async Task OnPostCreate_HonorsCatalogPolicy(BoardKind kind)
	{
		var model = await Board(kind);
		var result = await model.OnPostCreateAsync("My item", "details", 50, default);

		if (WorkflowCatalog.QuickAddAllowed(kind))
		{
			result.Should().NotBeOfType<BadRequestResult>();
			ActiveNodeCount(model.Board).Should().Be(1); // the quick-add wrote a node
		}
		else
		{
			result.Should().BeOfType<BadRequestResult>();
			ActiveNodeCount(model.Board).Should().Be(0); // gated off — nothing written
		}
	}

	[Fact]
	public async Task QuickAdd_OnFreeBoard_StaysPending()
	{
		// The actual create semantics on the allowed path (distinct from the gate above).
		var model = await Board(BoardKind.Free);
		await model.OnPostCreateAsync("My item", "details", 50, default);
		var n = _store.GetContext("proj").PlanNodes.Where(x => x.Board == model.Board && x.ActiveTo == null).ToList().Single();
		n.Status.Should().Be("Pending"); // free boards keep the legacy default
		n.Type.Should().BeEmpty();
	}
}
