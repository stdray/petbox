using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Tasks;

// spec methodology-active-instance: the project's explicit "which instance is active"
// pointer. Controls DEFAULTS only (GetRuntimeAsync / GetMethodologyGuideAsync with no
// `name`) — NEVER board membership rules, which always resolve through a board's own
// TaskBoards.MethodologyInstance regardless of what is active here. Replaces the former
// "N open instances -> merge kinds/linkKinds/tagAxes" heuristic with an explicit pointer;
// an unresolved ambiguity (N open, no valid pointer) is now a visible state, not a silent
// blend.
public sealed class MethodologyActiveInstanceTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public MethodologyActiveInstanceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mactive-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db.Factory(), _factory), new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static IHttpContextAccessor Http(string scopes)
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", scopes)], "test");
		var ctx = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) };
		ctx.Request.Scheme = "https";
		ctx.Request.Host = new HostString("box.test");
		return new HttpContextAccessor { HttpContext = ctx };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Tasks"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}

	// One kind, one status pair, distinguishable by its kind slug — so a test can tell WHICH
	// instance's definition resolved without inspecting the whole document.
	static MethodologyDefinition TinyDef(string name, string kindSlug) => new(name,
	[
		new MethodologyKindDef(kindSlug, QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(
				["task"],
				[
					new WorkflowStatus("Todo", "Todo", StatusKind.Open),
					new WorkflowStatus("Done", "Done", StatusKind.TerminalOk),
				],
				[new MethodologyTransitionDef("Todo", "Done")]),
		]),
	]);

	[Fact]
	public async Task NoInstances_NoPointer_ResolvesPresets()
	{
		var runtime = await _tasks.GetRuntimeAsync(Proj);
		runtime.IsDefinedKind("anything").Should().BeFalse();

		var guide = await _tasks.GetMethodologyGuideAsync(Proj);
		guide.Source.Should().Be("presets");
		guide.DefinitionVersion.Should().BeNull();
	}

	// This is the shape live on $system TODAY (2026-07-17): exactly one open instance, no
	// active pointer ever set. Resolution must stay unambiguous and unchanged — a single
	// open instance needs no explicit default.
	[Fact]
	public async Task ExactlyOneOpenInstance_NoPointer_ResolvesThatInstance_Unambiguous()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "solo", "template", await SeedTemplate("solo-tmpl", "onlykind"));

		var runtime = await _tasks.GetRuntimeAsync(Proj);
		runtime.IsDefinedKind("onlykind").Should().BeTrue();

		var guide = await _tasks.GetMethodologyGuideAsync(Proj);
		guide.Source.Should().Be("instance");
		guide.DefinitionVersion.Should().NotBeNull();
		guide.Markdown.Should().Contain("onlykind");
	}

	// The heuristic this replaces would have silently merged kindA + kindB. Now: an explicit,
	// visible "ambiguous" state — GetRuntimeAsync falls back to bare presets (no blend), and
	// the guide names both open instances instead of rendering a merged document.
	[Fact]
	public async Task TwoOpenInstances_NoPointer_IsExplicitlyAmbiguous_NeverSilentlyMerged()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "template", await SeedTemplate("tmpl-a", "kinda"));
		await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "template", await SeedTemplate("tmpl-b", "kindb"));

		var runtime = await _tasks.GetRuntimeAsync(Proj);
		runtime.IsDefinedKind("kinda").Should().BeFalse("no silent merge — neither instance's kind is defined on bare presets");
		runtime.IsDefinedKind("kindb").Should().BeFalse();

		var guide = await _tasks.GetMethodologyGuideAsync(Proj);
		guide.Source.Should().Be("ambiguous");
		guide.DefinitionVersion.Should().BeNull();
		guide.Markdown.Should().Contain("alpha");
		guide.Markdown.Should().Contain("beta");
		guide.Invariants.Should().BeEmpty();
	}

	[Fact]
	public async Task ActivePointer_ExplicitWin_OverTwoOpenInstances()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "template", await SeedTemplate("tmpl-a2", "kinda"));
		await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "template", await SeedTemplate("tmpl-b2", "kindb"));

		var before = await _tasks.GetActiveMethodologyInstanceAsync(Proj);
		before.Name.Should().BeNull();
		before.Version.Should().Be(0);

		var ack = await _tasks.SetActiveMethodologyInstanceAsync(Proj, "beta", before.Version);
		ack.Name.Should().Be("beta");
		ack.Changed.Should().BeTrue();

		var runtime = await _tasks.GetRuntimeAsync(Proj);
		runtime.IsDefinedKind("kindb").Should().BeTrue();
		runtime.IsDefinedKind("kinda").Should().BeFalse();

		var guide = await _tasks.GetMethodologyGuideAsync(Proj);
		guide.Source.Should().Be("active");
		guide.Markdown.Should().Contain("kindb");
	}

	[Fact]
	public async Task SetActive_RejectsMissingOrClosedInstance()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "one", "builtin", "classic");
		await _tasks.CloseMethodologyInstanceAsync(Proj, "one");

		var missing = () => _tasks.SetActiveMethodologyInstanceAsync(Proj, "nope", 0);
		(await missing.Should().ThrowAsync<ArgumentException>()).WithMessage("*not found*");

		var closed = () => _tasks.SetActiveMethodologyInstanceAsync(Proj, "one", 0);
		(await closed.Should().ThrowAsync<ArgumentException>()).WithMessage("*closed*OPEN*");
	}

	// The pointer's own invariant ("must reference an open instance") is enforced at WRITE
	// time. But drift is possible: an instance can close AFTER being made active. Read-side
	// resolution must not silently follow a now-closed pointer — it treats it as absent
	// (falls back to the single/none/ambiguous cases), while the raw stored pointer stays
	// visible via GetActiveMethodologyInstanceAsync so an operator can see and fix it.
	[Fact]
	public async Task StalePointer_TargetClosedAfterActivation_TreatedAsAbsent()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "solo", "template", await SeedTemplate("tmpl-stale", "solokind"));
		var ack = await _tasks.SetActiveMethodologyInstanceAsync(Proj, "solo", 0);
		ack.Name.Should().Be("solo");

		await _tasks.CloseMethodologyInstanceAsync(Proj, "solo");

		// Raw pointer still names "solo" (drift is visible, not silently cleared).
		var raw = await _tasks.GetActiveMethodologyInstanceAsync(Proj);
		raw.Name.Should().Be("solo");

		// But resolution treats it as absent: 0 open instances now -> presets.
		var runtime = await _tasks.GetRuntimeAsync(Proj);
		runtime.IsDefinedKind("solokind").Should().BeFalse();

		var guide = await _tasks.GetMethodologyGuideAsync(Proj);
		guide.Source.Should().Be("presets");
	}

	// THE hard boundary from the spec: the active pointer controls DEFAULTS only. A board
	// that belongs to instance X always resolves X's rules through its own membership, even
	// while Y is the project's active instance.
	[Fact]
	public async Task ActivePointer_NeverOverridesBoardMembership()
	{
		var alpha = await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "template", await SeedTemplate("tmpl-a3", "kinda"));
		var beta = await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "template", await SeedTemplate("tmpl-b3", "kindb"));

		// Make alpha active...
		await _tasks.SetActiveMethodologyInstanceAsync(Proj, "alpha", 0);

		// ...but a board that belongs to beta must still resolve beta's rules.
		var betaBoard = beta.Boards.Single().Name;
		var boardRuntime = await _tasks.GetRuntimeForBoardAsync(Proj, betaBoard);
		boardRuntime.IsDefinedKind("kindb").Should().BeTrue("board membership always wins over the active-instance default");
		boardRuntime.IsDefinedKind("kinda").Should().BeFalse();

		// And the project-level default (no board in hand) still resolves alpha.
		var projectRuntime = await _tasks.GetRuntimeAsync(Proj);
		projectRuntime.IsDefinedKind("kinda").Should().BeTrue();

		_ = alpha;
	}

	[Fact]
	public async Task SetActive_ThenClear_RoundTrips_WithCasVersioning()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "solo", "builtin", "classic");
		await _tasks.CreateMethodologyInstanceAsync(Proj, "solo2", "builtin", "classic");

		var setA = await _tasks.SetActiveMethodologyInstanceAsync(Proj, "solo", 0);
		setA.Name.Should().Be("solo");
		var setB = await _tasks.SetActiveMethodologyInstanceAsync(Proj, "solo2", setA.Version);
		setB.Name.Should().Be("solo2");
		setB.Version.Should().BeGreaterThan(setA.Version);

		// A clear whose baseline is behind the pointer's actual current revision is Stale
		// (baseline 0 is the one that means "force" on a delete — a non-zero stale baseline
		// is not).
		var stale = () => _tasks.SetActiveMethodologyInstanceAsync(Proj, null, setA.Version);
		(await stale.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*stale*");

		var cleared = await _tasks.SetActiveMethodologyInstanceAsync(Proj, null, setB.Version);
		cleared.Name.Should().BeNull();
		cleared.Changed.Should().BeTrue();

		var after = await _tasks.GetActiveMethodologyInstanceAsync(Proj);
		after.Name.Should().BeNull();

		// Idempotent second clear: no-op, not an error.
		var again = await _tasks.SetActiveMethodologyInstanceAsync(Proj, null, cleared.Version);
		again.Changed.Should().BeFalse();
	}

	[Fact]
	public async Task Mcp_ActiveGetSet_RoundTrip()
	{
		// methodology:write — set_active moves the pointer the agent-facing guide resolves
		// through, so it is governance-gated (spec methodology-write-scope). This suite covers
		// the pointer's CAS round-trip; the authz boundary lives in McpModuleToolsTests.
		var http = Http("tasks:read tasks:write methodology:write");
		var flags = Flags();

		await _tasks.CreateMethodologyInstanceAsync(Proj, "mcp-inst", "builtin", "classic");

		var before = await TasksTools.MethodologyActiveGetAsync(http, flags, _tasks, Proj);
		before.Name.Should().BeNull();

		var set = await TasksTools.MethodologySetActiveAsync(http, flags, _tasks, Proj, "mcp-inst", before.Version);
		set.Name.Should().Be("mcp-inst");
		set.Changed.Should().BeTrue();

		var after = await TasksTools.MethodologyActiveGetAsync(http, flags, _tasks, Proj);
		after.Name.Should().Be("mcp-inst");

		var missing = () => TasksTools.MethodologySetActiveAsync(http, flags, _tasks, Proj, "ghost", 0);
		(await missing.Should().ThrowAsync<ArgumentException>()).WithMessage("*not found*");
	}

	// Stores a tiny single-kind template and returns its key, ready to hand to
	// CreateMethodologyInstanceAsync(source: "template").
	async Task<string> SeedTemplate(string key, string kindSlug)
	{
		await _tasks.UpsertMethodologyTemplateAsync(Proj, key, TinyDef(key, kindSlug), 0);
		return key;
	}
}
