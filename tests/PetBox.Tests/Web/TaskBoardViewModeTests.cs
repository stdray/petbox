using LinqToDB;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Pages.ProjectHome;

namespace PetBox.Tests.Web;

// board-view-modes / board-view-persistence, end to end through TaskBoardModel: the resolution
// order (explicit `?view=` -> the board kind's methodology defaultView -> the builtin Tree
// default), and the "never a 500" promise for an unknown/reserved-but-unshipped mode in the URL.
public sealed class TaskBoardViewModeTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public TaskBoardViewModeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-viewmode-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db, _factory);
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

	TaskBoardModel Model(string board) =>
		new(Flags(), _tasks, new CommentService(_factory), new NullSettingsResolver()) { WorkspaceKey = "ws", ProjectKey = Proj, Board = board };

	static MethodologyKindDef CustomKind(string slug, string? defaultView) =>
		new(slug, QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["task"],
				[new("Todo", "Todo", StatusKind.Open), new("Done", "Done", StatusKind.TerminalOk)],
				[new("Todo", "Done")]),
		])
		{ DefaultView = defaultView };

	// Board-runtime resolution (RuntimeForBoardAsync) reads a board's METHODOLOGY INSTANCE
	// membership, not the legacy project-singleton def (that dual-read was retired — see
	// TasksService.RuntimeAsync's "Never fall back to methodology_defs"). So exercising a
	// custom kind's defaultView needs the real instance path: stash the definition as a
	// template, then create a named instance from it — the same act that auto-provisions the
	// kind's board with MethodologyInstance membership set (tasks_methodology_create).
	async Task<string> CreateInstanceBoard(string instanceName, string kindSlug, string? defaultView)
	{
		var templateKey = instanceName + "-tmpl";
		await _tasks.UpsertMethodologyTemplateAsync(Proj, templateKey,
			new MethodologyDefinition(templateKey, [CustomKind(kindSlug, defaultView)]), 0);
		var ack = await _tasks.CreateMethodologyInstanceAsync(Proj, instanceName, "template", templateKey);
		return ack.Boards.Single().Name;
	}

	// ── (a) resolve chain: explicit choice -> methodology defaultView -> builtin default ──

	[Fact]
	public async Task NoExplicitChoice_NoMethodologyDefault_ResolvesToTree()
	{
		await _store.CreateAsync(Proj, "b1", null, "simple");
		var m = Model("b1");
		await m.OnGetAsync(default);
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Tree);
		m.IsTagView.Should().BeFalse();
	}

	[Fact]
	public async Task NoExplicitChoice_MethodologyDefaultViewApplies()
	{
		var board = await CreateInstanceBoard("inst1", "custom", BoardViewModeNames.Tags);
		var m = Model(board);
		// No `by` supplied, so even though defaultView resolves to "tags", the by-validity
		// fallback (existing tag-grouping behavior) keeps the content pane on tree — but
		// ResolvedViewMode itself still reports the methodology's choice.
		await m.OnGetAsync(default);
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Tags);
		m.IsTagView.Should().BeFalse();
		m.ContentPartialName.Should().Be("_BoardViewTree");
	}

	[Fact]
	public async Task ExplicitChoice_WinsOverMethodologyDefaultView()
	{
		var board = await CreateInstanceBoard("inst2", "custom2", BoardViewModeNames.Tags);
		var m = Model(board);
		m.ViewMode = BoardViewModeNames.Tree; // explicit — overrides the kind's defaultView
		await m.OnGetAsync(default);
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Tree);
	}

	[Fact]
	public async Task ReservedButUnshippedMethodologyDefault_DegradesToTree()
	{
		// intake's own preset default is "table" (methodology-default-view-field), which has
		// no PetBox.Web partial yet — resolution must not surface "table" as something the
		// page tries (and fails) to render.
		await _store.CreateAsync(Proj, "b4", null, "intake");
		var m = Model("b4");
		var result = await m.OnGetAsync(default);
		result.Should().NotBeNull();
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Tree);
		m.ContentPartialName.Should().Be("_BoardViewTree");
	}

	// ── (d) an unknown/unsupported mode in the URL never 500s ──────────────────

	[Theory]
	[InlineData("bogus")]
	[InlineData("kanban")] // known name, no renderer yet
	public async Task UnknownOrUnshippedUrlViewMode_FallsBackToTree_NoException(string requested)
	{
		await _store.CreateAsync(Proj, "b5", null, "simple");
		var m = Model("b5");
		m.ViewMode = requested;
		var result = await m.OnGetAsync(default);
		result.Should().NotBeNull();
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Tree);
		m.IsTagView.Should().BeFalse();
		m.ContentPartialName.Should().Be("_BoardViewTree");
	}
}
