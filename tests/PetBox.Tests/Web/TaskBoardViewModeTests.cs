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
using PetBox.Web.Rendering;

namespace PetBox.Tests.Web;

// board-view-modes / board-view-persistence, end to end through TaskBoardModel: the resolution
// order (explicit `?view=` -> the board kind's methodology defaultView -> the builtin Tree
// default), the "never a 500" promise for an unknown mode in the URL, and (board-view-mode-
// framework) kanban's columns / outline's reveal-mode parameterization.
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
	public async Task NoExplicitChoice_MethodologyDefaultView_Tags_IsDisabled_FallsBackToTree()
	{
		// board-tag-grouping-disabled: a methodology defaultView of "tags" used to leak through
		// into ResolvedViewMode even though the content pane degraded to tree (no `by`) — now
		// BoardViewModeRegistry.Resolve refuses the disabled entry at EVERY tier, so
		// ResolvedViewMode itself reports "tree", not just the rendered content.
		var board = await CreateInstanceBoard("inst1", "custom", BoardViewModeNames.Tags);
		var m = Model(board);
		await m.OnGetAsync(default);
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Tree);
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
	public async Task IntakeMethodologyDefault_TableIsNowShipped_ResolvesToTable()
	{
		// intake's own preset default is "table" (methodology-default-view-field). Table's
		// partial shipped with board-view-mode-framework's follow-up, so this now resolves to
		// (and renders) table instead of degrading — the degrade-to-tree behavior this test
		// used to pin is exercised by UnknownUrlViewMode_FallsBackToTree_NoException below with
		// a genuinely unregistered name instead.
		await _store.CreateAsync(Proj, "b4", null, "intake");
		var m = Model("b4");
		var result = await m.OnGetAsync(default);
		result.Should().NotBeNull();
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Table);
		m.ContentPartialName.Should().Be("_BoardViewTable");
	}

	// ── (d) an unknown mode in the URL never 500s ───────────────────────────────

	[Fact]
	public async Task UnknownUrlViewMode_FallsBackToTree_NoException()
	{
		await _store.CreateAsync(Proj, "b5", null, "simple");
		var m = Model("b5");
		m.ViewMode = "bogus";
		var result = await m.OnGetAsync(default);
		result.Should().NotBeNull();
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Tree);
		m.IsTagView.Should().BeFalse();
		m.ContentPartialName.Should().Be("_BoardViewTree");
	}

	[Fact]
	public async Task KanbanUrlViewMode_NowRenders_NotAFallback()
	{
		// kanban had no renderer when this suite was first written (see the removed
		// UnknownOrUnshippedUrlViewMode_FallsBackToTree_NoException("kanban") case); the
		// board-view-mode-framework follow-up shipped its partial, so an explicit ?view=kanban
		// now resolves to kanban itself instead of degrading.
		await _store.CreateAsync(Proj, "b6", null, "simple");
		var m = Model("b6");
		m.ViewMode = BoardViewModeNames.Kanban;
		var result = await m.OnGetAsync(default);
		result.Should().NotBeNull();
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Kanban);
		m.ContentPartialName.Should().Be("_BoardViewKanban");
	}

	// ── kanban: columns come from the board's OWN workflow, not hardcoded ──────

	[Fact]
	public async Task Kanban_ColumnsComeFromTheBoardsOwnWorkflow_NotHardcoded()
	{
		// A custom methodology whose statuses (Todo/Done) look nothing like the `work` preset's
		// own kanban columns (Pending/InProgress/Review/Done/Blocked/Cancelled) — proves
		// KanbanColumns is sourced from THIS board's workflow, not a hardcoded stage list.
		var board = await CreateInstanceBoard("kanbaninst", "kanbankind", BoardViewModeNames.Kanban);
		var m = Model(board);
		await m.OnGetAsync(default);

		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Kanban);
		m.ContentPartialName.Should().Be("_BoardViewKanban");
		m.KanbanColumns.Select(c => c.Slug).Should().Equal("Todo", "Done");
		m.KanbanColumns.Select(c => c.Slug).Should().NotContain(["Pending", "InProgress", "Review"]);
	}

	// ── outline: reveal mode follows the board's PRESET kind ───────────────────

	[Fact]
	public async Task Outline_SpecKind_UsesInlineLazyRevealMode()
	{
		await _store.CreateAsync(Proj, "specboard", null, "spec");
		var m = Model("specboard");
		m.ViewMode = BoardViewModeNames.Outline;
		await m.OnGetAsync(default);
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Outline);
		m.ContentPartialName.Should().Be("_BoardViewOutline");
		m.OutlineRevealMode.Should().Be(OutlineRevealModeNames.InlineLazy);
	}

	[Fact]
	public async Task Outline_NonSpecKind_UsesNavigateRevealMode()
	{
		await _store.CreateAsync(Proj, "simpleboard", null, "simple");
		var m = Model("simpleboard");
		m.ViewMode = BoardViewModeNames.Outline;
		await m.OnGetAsync(default);
		m.OutlineRevealMode.Should().Be(OutlineRevealModeNames.Navigate);
	}

	[Fact]
	public async Task Outline_CustomDefinedKind_ConservativelyUsesNavigateRevealMode()
	{
		// A definition-declared custom kind's body length is unknown — the conservative default
		// (navigate) applies even though it isn't the `spec` preset.
		var board = await CreateInstanceBoard("outlineinst", "outlinekind", BoardViewModeNames.Outline);
		var m = Model(board);
		await m.OnGetAsync(default);
		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Outline);
		m.OutlineRevealMode.Should().Be(OutlineRevealModeNames.Navigate);
	}

	// Regression (board-view-modes inline-lazy unreachable): every OTHER outline test above
	// creates its `spec` board directly against the store (`_store.CreateAsync(..., "spec")`),
	// which leaves the project on MethodologyRuntime.PresetsOnly — IsDefinedKind("spec") is
	// false there, so the OLD `Runtime.PresetKind(KindSlug) == BoardKind.Spec` check happened
	// to still work and the gap went unnoticed. A REAL spec board never looks like that: it is
	// provisioned by the standard quartet/classic template (EnableMethodologyAsync ->
	// MethodologyInstanceService.CreateAsync(source: builtin) -> MethodologyPresets.
	// RenderBuiltinTemplate), which stores every kind — including "spec" — as a materialized
	// MethodologyDefinition. That makes IsDefinedKind("spec") TRUE, so PresetKind("spec") used
	// to read null and the board fell to `navigate` — inline-lazy was dead code for every board
	// created the ordinary way. Runtime.OutlineReveal (data on MethodologyKindDef, propagated
	// through RenderPresetDefinition) fixes this by not routing through PresetKind at all.
	[Fact]
	public async Task Outline_SpecKind_ViaStandardQuartetTemplate_UsesInlineLazyRevealMode()
	{
		await _tasks.EnableMethodologyAsync(Proj, "quartet", default);
		var specBoard = (await _store.ListAsync(Proj, default)).Single(b => string.Equals(b.Kind, "spec", StringComparison.Ordinal));

		var m = Model(specBoard.Name);
		m.ViewMode = BoardViewModeNames.Outline;
		await m.OnGetAsync(default);

		m.ResolvedViewMode.Should().Be(BoardViewModeNames.Outline);
		m.ContentPartialName.Should().Be("_BoardViewOutline");
		m.OutlineRevealMode.Should().Be(OutlineRevealModeNames.InlineLazy);
	}
}
