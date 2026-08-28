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

// decision-pending-has-no-ui: the board page's "?decisionPending=" narrowing, end to end through
// TaskBoardModel — exercising the REAL LoadAsync path (GetAsync + the SearchNodesAsync/
// TaskNodeFilter.DecisionPending re-filter), not a string check against a mock. Model.Nodes is
// exactly the collection every view partial (_TaskNodeCard/_BoardViewKanban/_BoardViewOutline/
// _BoardViewTable) iterates, so proving a node is ABSENT from it (not merely flagged Hidden) is the
// server-predicate claim the card requires: "не клиентской фильтрацией уже выданного".
public sealed class TaskBoardDecisionPendingFilterTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public TaskBoardDecisionPendingFilterTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-decisionpendingui-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
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

	TaskBoardModel Model(string board, bool? decisionPendingParam = null) =>
		new(Flags(), _tasks, new CommentService(_factory), new NullSettingsResolver())
		{ WorkspaceKey = "ws", ProjectKey = Proj, Board = board, DecisionPendingParam = decisionPendingParam };

	static NodePatch Node(string key, bool? decisionPending = null, string? status = null, long version = 0) => new()
	{
		Key = key,
		Title = key,
		Body = "body",
		Status = status,
		DecisionPending = decisionPending,
		Version = version,
	};

	async Task<TaskNodeView> Read(string board, string key) =>
		(await _tasks.GetAsync(Proj, board, includeClosed: true)).Nodes.Single(n => n.Key == key);

	[Fact]
	public async Task NoFilter_DefaultRender_ShowsBothPendingAndPlainNodes()
	{
		await _store.CreateAsync(Proj, "b1", null, "simple");
		await _tasks.UpsertAsync(Proj, "b1", [Node("pending1", decisionPending: true), Node("plain1")]);

		var m = Model("b1");
		await m.OnGetAsync(default);

		m.DecisionPendingOnly.Should().BeFalse();
		m.Nodes.Select(n => n.Key).Should().BeEquivalentTo(["pending1", "plain1"]);
	}

	[Fact]
	public async Task Filtered_ExcludedNodeIsAbsentFromModelNodes_NotJustHidden()
	{
		await _store.CreateAsync(Proj, "b2", null, "simple");
		await _tasks.UpsertAsync(Proj, "b2", [Node("pending1", decisionPending: true), Node("plain1")]);

		var m = Model("b2", decisionPendingParam: true);
		await m.OnGetAsync(default);

		m.DecisionPendingOnly.Should().BeTrue();
		m.Nodes.Select(n => n.Key).Should().BeEquivalentTo(["pending1"],
			"the excluded node must not even be IN Model.Nodes — the SAME collection every card " +
			"partial iterates — a display-only Hidden flag would still leave it here");
	}

	// THE reverse-direction acceptance bullet: an enabled filter with zero matches must render
	// empty, never silently fall back to showing the whole board.
	[Fact]
	public async Task Filtered_NoPendingNodesAtAll_YieldsEmptyBoard_NotFullBoard()
	{
		await _store.CreateAsync(Proj, "b3", null, "simple");
		await _tasks.UpsertAsync(Proj, "b3", [Node("plain1"), Node("plain2")]);

		var m = Model("b3", decisionPendingParam: true);
		await m.OnGetAsync(default);

		m.Nodes.Should().BeEmpty();
	}

	// Parity with decision-pending-survives-closure: a CLOSED node that still carries the flag must
	// stay in the filtered queue — the same StatusKind widening this page's own unfiltered
	// GetAsync(includeClosed:true) already applies for the board's base read.
	[Fact]
	public async Task Filtered_KeepsAClosedNodeThatStillCarriesTheFlag()
	{
		await _store.CreateAsync(Proj, "b4", null, "simple");
		await _tasks.UpsertAsync(Proj, "b4", [Node("closed-pending", decisionPending: true)]);
		var born = await Read("b4", "closed-pending");
		await _tasks.UpsertAsync(Proj, "b4", [Node("closed-pending", status: "Done", version: born.Version)]);

		var m = Model("b4", decisionPendingParam: true);
		await m.OnGetAsync(default);

		m.Nodes.Select(n => n.Key).Should().BeEquivalentTo(["closed-pending"],
			"a Done node that still waits on the owner must not silently drop out of the filtered queue");
	}

	// board-view-cross-device shape parity: explicit `?decisionPending=false` must win over ANY
	// saved preference — proven here with the builtin default (NullSettingsResolver never has a
	// saved one), the persistence cascade itself is exercised by TaskBoard's existing view/fields
	// tests (BoardViewCrossDeviceTests) which this filter deliberately mirrors.
	[Fact]
	public async Task ExplicitFalse_NeverNarrows_EvenWithPendingNodesPresent()
	{
		await _store.CreateAsync(Proj, "b5", null, "simple");
		await _tasks.UpsertAsync(Proj, "b5", [Node("pending1", decisionPending: true), Node("plain1")]);

		var m = Model("b5", decisionPendingParam: false);
		await m.OnGetAsync(default);

		m.DecisionPendingOnly.Should().BeFalse();
		m.Nodes.Select(n => n.Key).Should().BeEquivalentTo(["pending1", "plain1"]);
	}
}
