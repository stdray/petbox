using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
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

// ui-methodology-runtime-unify: the board UI resolves kind/terminality/quick-add/next-status
// through ITasksService.GetRuntimeAsync (the SAME MethodologyRuntime the MCP tools use), not
// the MethodologyPresets statics. A DEFINITION-declared custom kind must therefore answer the
// UI's questions from its own data instead of collapsing to the `Simple` preset fallback.
// These are service-door tests: define a methodology with a custom kind, then assert both the
// raw runtime answers and the TaskBoard PageModel that wires them.
public sealed class MethodologyRuntimeUiTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly TasksService _tasks;

	public MethodologyRuntimeUiTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-uiruntime-" + Guid.NewGuid().ToString("N"));
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

	// A custom kind `risk`: quick-add OFF, its own vocab with two TERMINAL slugs (`Mitigated`
	// terminalok, `Dropped` terminalcancel) that the presets don't know — the exact shape the
	// preset fallback would misclassify (custom terminals read as non-terminal, quick-add wrongly
	// allowed, kind badge lies "simple").
	static MethodologyDefinition RiskDefinition() => new(
		"acme-risk",
		[
			new MethodologyKindDef(
				Kind: "risk",
				QuickAddAllowed: false,
				Workflows:
				[
					new MethodologyWorkflowDef(
						Types: ["risk"],
						Statuses:
						[
							new WorkflowStatus("Open", "Open", StatusKind.Open),
							new WorkflowStatus("Assessing", "Assessing", StatusKind.Open),
							new WorkflowStatus("Mitigated", "Mitigated", StatusKind.TerminalOk),
							new WorkflowStatus("Dropped", "Dropped", StatusKind.TerminalCancel),
						],
						Transitions:
						[
							new MethodologyTransitionDef("Open", "Assessing"),
							new MethodologyTransitionDef("Assessing", "Mitigated"),
							new MethodologyTransitionDef("Assessing", "Dropped"),
						]),
				]),
		]);

	// 1. The runtime the UI now resolves through answers a definition-declared kind from its
	//    OWN data — every question the board/node pages ask.
	[Fact]
	public async Task GetRuntime_DefinedKind_AnswersFromDefinition()
	{
		await InstallRiskInstanceAsync();
		var runtime = await _tasks.GetRuntimeAsync(Proj);

		// Kind badge names the custom slug (not the `simple` fallback).
		runtime.KindName("risk").Should().Be("risk");
		runtime.PresetKind("risk").Should().BeNull("a definition-declared kind has no process role");

		// Custom terminal statuses ARE terminal (drives active-only hiding + the closed badge).
		runtime.IsTerminalStatus("risk", "Mitigated").Should().BeTrue();
		runtime.IsTerminalStatus("risk", "Dropped").Should().BeTrue();
		runtime.IsTerminalStatus("risk", "Open").Should().BeFalse();
		runtime.IsTerminalStatus("risk", "Assessing").Should().BeFalse();
		runtime.StatusKindOf("risk", "Mitigated").Should().Be(StatusKind.TerminalOk);
		runtime.StatusKindOf("risk", "Dropped").Should().Be(StatusKind.TerminalCancel);

		// Quick-add follows the kind's own policy (OFF here).
		runtime.QuickAddAllowed("risk").Should().BeFalse();

		// The node page's NextStatuses come from the kind's own FSM.
		var wf = runtime.For("risk", "risk");
		wf.Should().NotBeNull();
		wf!.NextFrom("Assessing").Should().BeEquivalentTo("Mitigated", "Dropped");
		wf.NextFrom("Open").Should().BeEquivalentTo(["Assessing"]);
	}

	// 2. A definition that declares `risk` leaves the built-in presets untouched — the same
	//    runtime still answers preset kinds exactly as MethodologyPresets always did (criterion 3).
	[Fact]
	public async Task GetRuntime_PresetKinds_Unchanged()
	{
		await InstallRiskInstanceAsync();
		var runtime = await _tasks.GetRuntimeAsync(Proj);

		runtime.KindName("simple").Should().Be("simple");
		runtime.PresetKind("simple").Should().Be(BoardKind.Simple);
		runtime.QuickAddAllowed("simple").Should().BeTrue();
		runtime.IsTerminalStatus("simple", "Done").Should().BeTrue();
		runtime.IsTerminalStatus("simple", "InProgress").Should().BeFalse();

		// Spec preset stays a quartet kind (the _PlanNodeCard spec-noise guard keys on this).
		runtime.PresetKind("spec").Should().Be(BoardKind.Spec);
		runtime.QuickAddAllowed("spec").Should().BeFalse();
	}

	// ui-terminology-pass: StatusName is the ONE slug→label mapping the status badge and the
	// status-change select render through, so PascalCase preset slugs and lowercase methodology
	// slugs read consistently. Preset slugs resolve to their declared Name; a defined kind uses
	// its OWN status Name; an out-of-vocab slug falls back to the slug verbatim.
	[Fact]
	public async Task StatusName_ResolvesDeclaredLabel_ForPreset_Defined_AndFallback()
	{
		var def = new MethodologyDefinition(
			"acme-label",
			[
				new MethodologyKindDef("support", QuickAddAllowed: false,
				[
					new MethodologyWorkflowDef(["ticket"],
						[
							new WorkflowStatus("waiting", "Waiting on customer", StatusKind.Open),
							new WorkflowStatus("closed", "Closed", StatusKind.TerminalOk),
						],
						[new MethodologyTransitionDef("waiting", "closed")]),
				]),
			]);
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "label-tmpl", def, 0);
		await _tasks.CreateMethodologyInstanceAsync(Proj, "labels", "template", "label-tmpl");
		var runtime = await _tasks.GetRuntimeAsync(Proj);

		// Preset labels: PascalCase → sentence case, lowercase quartet → capitalized, per the
		// declared WorkflowStatus.Name — never the raw slug.
		runtime.StatusName("simple", "InProgress").Should().Be("In progress");
		runtime.StatusName("spec", "defined").Should().Be("Defined");
		runtime.StatusName("intake", "wontfix").Should().Be("Won't fix");

		// A defined kind uses its own declared Name (which may differ from the slug entirely).
		runtime.StatusName("support", "waiting").Should().Be("Waiting on customer");
		runtime.StatusName("support", "closed").Should().Be("Closed");

		// An unknown / legacy slug is shown verbatim (never blanked).
		runtime.StatusName("simple", "LegacyLimbo").Should().Be("LegacyLimbo");
	}

	// 3. The TaskBoard PageModel — the actual UI surface — renders a custom-kind board off the
	//    runtime: the kind badge shows the custom slug, it is NOT the simple preset, and quick-add
	//    is gated by the kind's own policy (all previously wrong under the preset fallback).
	[Fact]
	public async Task TaskBoardModel_DefinedKind_ResolvesThroughRuntime()
	{
		await InstallRiskInstanceAsync();
		// Single-kind instance board is named after the instance ("risks").
		var board = (await _tasks.ListBoardsAsync(Proj)).Single(b => b.Kind == "risk").Name;

		var model = new TaskBoardModel(Flags(), _tasks, new CommentService(_factory), new NullSettingsResolver())
		{ WorkspaceKey = "ws", ProjectKey = Proj, Board = board };
		await model.OnGetAsync(default);

		model.KindSlug.Should().Be("risk");
		model.KindName.Should().Be("risk", "the badge names the custom kind, not `simple`");
		model.Runtime.PresetKind(model.KindSlug).Should().BeNull("a defined kind has no preset process role");
		model.ShowQuickAdd.Should().BeFalse("the custom kind sets quickAddAllowed:false");
		// The wired runtime is the instance-aware one, so its per-board terminality is honored.
		model.Runtime.IsTerminalStatus(model.KindSlug, "Mitigated").Should().BeTrue();
	}

	// 3b. The POST quick-add gate honors the custom kind's policy too (not the `simple` fallback,
	//     which would wrongly allow the write).
	[Fact]
	public async Task TaskBoardModel_QuickAddPost_GatedByDefinedPolicy()
	{
		await InstallRiskInstanceAsync();
		var board = (await _tasks.ListBoardsAsync(Proj)).Single(b => b.Kind == "risk").Name;

		var model = new TaskBoardModel(Flags(), _tasks, new CommentService(_factory), new NullSettingsResolver())
		{ WorkspaceKey = "ws", ProjectKey = Proj, Board = board };
		Wire(model);
		var result = await model.OnPostCreateAsync("a risk", "body", 50, default);

		result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestResult>();
		_store.GetContext(Proj).PlanNodes.Count(n => n.Board == board && n.ActiveTo == null)
			.Should().Be(0, "quick-add is gated off for this kind — nothing written");
	}

	// spec methodology-inactive-visibility: TaskBoardModel.InstanceInactive is a COMPUTED view —
	// true only when the board's own OPEN instance is not the project's current effective
	// default (ResolveDefaultMethodologyInstanceAsync). Never a stored flag; never true for a
	// board with no instance membership.
	[Fact]
	public async Task TaskBoardModel_InstanceInactive_TracksEffectiveDefault_NotBoardState()
	{
		var alpha = await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "builtin", "classic");
		await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "builtin", "classic");
		var alphaBoard = alpha.Boards.Single().Name;

		// Two open instances, no pointer -> ambiguous, no default: alpha's own board reads inactive.
		var page = new TaskBoardModel(Flags(), _tasks, new CommentService(_factory), new NullSettingsResolver())
		{ WorkspaceKey = "ws", ProjectKey = Proj, Board = alphaBoard };
		await page.OnGetAsync(default);
		page.InstanceInactive.Should().BeTrue("no effective default exists while two instances are open and no pointer is set");

		// Make alpha itself the active pointer -> its own board is no longer "inactive".
		await _tasks.SetActiveMethodologyInstanceAsync(Proj, "alpha", 0);
		var page2 = new TaskBoardModel(Flags(), _tasks, new CommentService(_factory), new NullSettingsResolver())
		{ WorkspaceKey = "ws", ProjectKey = Proj, Board = alphaBoard };
		await page2.OnGetAsync(default);
		page2.InstanceInactive.Should().BeFalse("alpha is now the project's active default");
	}

	async Task InstallRiskInstanceAsync()
	{
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "risk-tmpl", RiskDefinition(), 0);
		await _tasks.CreateMethodologyInstanceAsync(Proj, "risks", "template", "risk-tmpl");
	}

	// Sysadmin claim: OnPostCreateAsync now guards itself to Member+ (viewer-member-consistency)
	// via User.HasWorkspaceRoleAtLeast — an unwired PageModel has no PageContext at all, which
	// this test's assertion (BadRequest from the KIND's own quick-add policy) never used to need.
	// Sysadmin is the universal free-pass, so the guard stays out of the way of what's under test.
	static void Wire(PageModel page)
	{
		var identity = new ClaimsIdentity([new Claim(PetBoxClaims.IsSysAdmin, "true")], "Test");
		page.PageContext = new PageContext
		{
			HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
		};
	}
}
