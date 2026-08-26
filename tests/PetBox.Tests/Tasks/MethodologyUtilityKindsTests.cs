using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// spec methodology-utility-kinds: a board is a member of EXACTLY one world — a methodology
// instance, OR the project's utility layer (TaskBoardMeta.UtilityWorld, "$utility") — never
// a whole-object substitute, never inherited from whichever instance happens to be active.
// The reserved sentinel is a NEW, deliberate world, distinct from (and not a replacement for)
// the legacy null-membership bootstrap state MethodologyInstanceBackfillTests covers, whose
// old behavior (RuntimeAsync's active-instance/presets heuristic, never methodology_defs —
// LegacyUnassignedBoard_IgnoresProjectSingletonAxes in MethodologyInstanceTests) must stay
// unchanged.
public sealed class MethodologyUtilityKindsTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public MethodologyUtilityKindsTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mutil-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
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

	[Fact]
	public async Task CreateBoard_UtilitySentinel_AllowedEvenWithOpenInstance()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "quartet");

		// Bare null is rejected once an instance exists (unchanged legacy gate)...
		var bare = () => _tasks.CreateBoardAsync(Proj, "scratch", "simple", null, null);
		await bare.Should().ThrowAsync<ArgumentException>();

		// ...but the explicit utility sentinel is ALWAYS legal, regardless of instances.
		var meta = await _tasks.CreateBoardAsync(Proj, "scratch", "simple", null, null, TaskBoardMeta.UtilityWorld);
		meta.MethodologyInstance.Should().Be(TaskBoardMeta.UtilityWorld);

		var boards = await _tasks.ListBoardsAsync(Proj);
		boards.Single(b => b.Name == "scratch").MethodologyInstance.Should().Be("$utility");
	}

	[Fact]
	public async Task UtilityBoard_ResolvesCustomKind_IndependentOfActiveInstanceSwitch()
	{
		// Declare a custom "wiki"-like kind in the project's utility layer — homed on the
		// project, not inside any instance.
		var def = new MethodologyDefinition("utility",
		[
			new MethodologyKindDef("wiki", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["page"],
				[
					new WorkflowStatus("draft", "Draft", StatusKind.Open),
					new WorkflowStatus("live", "Live", StatusKind.Open),
				],
				[
					new MethodologyTransitionDef("draft", "live"),
				]),
			]),
		]);
		await _tasks.DefineMethodologyAsync(Proj, def, 0);

		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "quartet");
		var board = await _tasks.CreateBoardAsync(Proj, "wiki", "wiki", null, null, TaskBoardMeta.UtilityWorld);
		board.Kind.Should().Be("wiki");
		board.MethodologyInstance.Should().Be(TaskBoardMeta.UtilityWorld);

		// The custom workflow resolves for this board (status "live" is only valid because
		// the utility definition declares it).
		var write = await _tasks.UpsertAsync(Proj, "wiki",
		[
			new NodePatch { Key = "p1", Title = "Page 1", Type = "page", Status = "live", Body = "x" },
		]);
		write.Result.Applied.Should().BeTrue();

		// Switching (closing) the active/only instance must NOT change the utility board's
		// resolution — it is structurally outside the instance, not merely un-touched by luck.
		await _tasks.CloseMethodologyInstanceAsync(Proj, "main");
		var write2 = await _tasks.UpsertAsync(Proj, "wiki",
		[
			new NodePatch { Key = "p2", Title = "Page 2", Type = "page", Status = "draft", Body = "y" },
		]);
		write2.Result.Applied.Should().BeTrue();
	}

	[Fact]
	public async Task AdoptToUtility_ReleasesBoardFromInstance_EnforcesSingletonInUtilityBucket()
	{
		var ack = await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "quartet");
		var wiredBoard = ack.Boards.Single(b => b.Kind == "spec").Name;

		var released = await _tasks.AdoptBoardAsync(Proj, wiredBoard, TaskBoardMeta.UtilityWorld);
		released.MethodologyInstance.Should().Be(TaskBoardMeta.UtilityWorld);

		// spec is a Singleton kind (methodology-kind-singleton) — a second spec board cannot
		// join the SAME utility bucket while this one is open.
		var dup = () => _tasks.CreateBoardAsync(Proj, "extra-spec", "spec", null, null, TaskBoardMeta.UtilityWorld);
		(await dup.Should().ThrowAsync<ArgumentException>()).WithMessage("*utility*");

		// Idempotent: releasing an already-utility board again is a no-op, not an error.
		var again = await _tasks.AdoptBoardAsync(Proj, wiredBoard, TaskBoardMeta.UtilityWorld);
		again.MethodologyInstance.Should().Be(TaskBoardMeta.UtilityWorld);
	}

	[Fact]
	public async Task AdoptToUtility_RejectsUndeclaredCustomKind()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "quartet");
		// Declare a custom kind directly on the instance's own rules (the "wiki-lives-on-
		// quartet" muddle the spec calls out) — releasing it to utility BEFORE the utility
		// layer declares the same kind must fail loudly, not strand every node on the board.
		var rules = await _tasks.GetMethodologyInstanceRulesAsync(Proj, "main");
		var withWiki = rules!.Definition with
		{
			Kinds = rules.Definition.Kinds.Append(new MethodologyKindDef("wiki", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["page"],
				[
					new WorkflowStatus("draft", "Draft", StatusKind.Open),
				], [])
			])).ToList(),
		};
		await _tasks.DefineMethodologyInstanceRulesAsync(Proj, "main", withWiki, rules.Version);
		var wikiBoard = await _tasks.CreateBoardAsync(Proj, "wiki", "wiki", null, null, "main");

		var release = () => _tasks.AdoptBoardAsync(Proj, wikiBoard.Name, TaskBoardMeta.UtilityWorld);
		(await release.Should().ThrowAsync<ArgumentException>()).WithMessage("*wiki*utility*");
	}

	[Fact]
	public async Task Mcp_UtilityGetUpsert_RoundTrip_AndBoardAdoptToUtility()
	{
		var http = Http("tasks:read tasks:write methodology:write");
		var flags = Flags();

		// Addressed read: no utility layer defined yet is a clear error, not Found=false
		// (batch2 not-found-two-contracts-under-tasks — tasks_methodology_utility_get now
		// matches tasks_node_get's contract instead of the old nullable-get one).
		// custom-kind-route-undiscoverable: the addressed-read contract stays an error (not
		// found:false / an empty document) — only the text changes, to name the fix instead of
		// just the absence.
		var miss = () => TasksTools.MethodologyUtilityGetAsync(http, flags, _tasks, Proj);
		(await miss.Should().ThrowAsync<ArgumentException>())
			.WithMessage($"*{Proj}*has no utility-kind layer defined*tasks_methodology_utility_upsert*version: 0*");

		var input = new MethodologyDefInput
		{
			Name = "utility",
			Kinds =
			[
				new MethodologyKindInput
				{
					Kind = "wiki",
					QuickAddAllowed = true,
					Workflows =
					[
						new MethodologyWorkflowInput
						{
							Types = ["page"],
							Statuses = [new MethodologyStatusInput { Slug = "draft", Kind = "open" }],
							Transitions = [],
						},
					],
				},
			],
		};
		var upserted = await TasksTools.MethodologyUtilityUpsertAsync(http, flags, _tasks, Proj, input, 0);
		upserted.Changed.Should().BeTrue();

		var got = await TasksTools.MethodologyUtilityGetAsync(http, flags, _tasks, Proj);
		got.Kinds!.Should().ContainSingle(k => k.Kind == "wiki");

		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "quartet");
		var created = await TasksTools.BoardCreateAsync(http, flags, _tasks, Proj, "wiki", "wiki", null, null, TaskBoardMeta.UtilityWorld);
		created.MethodologyInstance.Should().Be(TaskBoardMeta.UtilityWorld);

		var adopted = await TasksTools.BoardAdoptAsync(http, flags, _tasks, Proj, "wiki", TaskBoardMeta.UtilityWorld);
		adopted.MethodologyInstance.Should().Be(TaskBoardMeta.UtilityWorld); // already there — idempotent no-op
	}

	[Fact]
	public async Task Mcp_BoardAdoptToUtility_RequiresMethodologyWriteScope()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "quartet");
		var httpNoGov = Http("tasks:read tasks:write");
		var flags = Flags();

		var noScope = () => TasksTools.BoardAdoptAsync(httpNoGov, flags, _tasks, Proj, "main-spec", TaskBoardMeta.UtilityWorld);
		// Board name unknown here — the scope assertion must fire before any lookup either way.
		await noScope.Should().ThrowAsync<Exception>();
	}

	// custom-kind-route-undiscoverable (search-kind-resolution-ignores-utility-layer): the LIVE
	// defect. tasks_board_list and tasks_workflow both resolve a board's kind through
	// RuntimeForBoardAsync (per-board: utility sentinel -> utility layer, else the board's own
	// instance) and got it right; tasks_search's listing/query response header used a SINGLE
	// project-level runtime (RuntimeAsync — the ACTIVE instance, or presets) for every board in
	// scope, which is simply the wrong authority for a board whose own membership disagrees with
	// it. `$system` never showed this because it declares `wiki` TWICE (utility layer AND the
	// active `quartet` instance's own rules) — the active-instance copy quietly caught what would
	// otherwise have missed, which is exactly why this test declares the kind in the utility layer
	// ONLY and puts a DIFFERENT active instance (`classic`, which does not know `wiki` at all) in
	// play: the one shape that actually reproduces the report from `petsonde`.
	[Fact]
	public async Task Search_BoardScopedKind_ResolvesUtilityOnlyCustomKind_NotShadowedByActiveInstance()
	{
		var def = new MethodologyDefinition("utility",
		[
			new MethodologyKindDef("wiki", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["page"],
				[
					new WorkflowStatus("draft", "Draft", StatusKind.Open),
					new WorkflowStatus("live", "Live", StatusKind.Open),
				],
				[
					new MethodologyTransitionDef("draft", "live"),
				]),
			]),
		]);
		await _tasks.DefineMethodologyAsync(Proj, def, 0);

		// The ACTIVE instance is "classic" — it declares no "wiki" kind anywhere, so a fix that
		// merely widened the active instance's OWN kind set would not catch this.
		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "classic");
		var board = await _tasks.CreateBoardAsync(Proj, "wiki", "wiki", null, null, TaskBoardMeta.UtilityWorld);
		board.Kind.Should().Be("wiki");

		await _tasks.UpsertAsync(Proj, "wiki",
		[
			new NodePatch { Key = "p1", Title = "Page 1", Type = "page", Status = "draft", Body = "x" },
		]);

		var boardScoped = await _tasks.SearchNodesAsync(Proj, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Filter = new TaskNodeFilter(Board: "wiki"),
		});
		boardScoped.Kind.Should().Be("wiki",
			"the board's kind is declared ONLY in the project's utility layer — a project-level " +
			"(active-instance) runtime must not shadow it back to the 'simple' preset default");

		// The same defect class, in query mode (q != null runs a DIFFERENT resolve path —
		// HybridCandidatesAsync/ProjectBoardLeanOpenAsync — that carried the identical bug).
		var queried = await _tasks.SearchNodesAsync(Proj, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Query = "Page 1",
			Filter = new TaskNodeFilter(Board: "wiki"),
		});
		queried.Kind.Should().Be("wiki");
		queried.Hits.Should().Contain(h => h.Node.Key == "p1");
	}

	// Sibling of the regression above, in the PRESET-ONLY shape (board-kind-dependent behavior is
	// covered in both forms per project convention): a bare preset kind, declared NOWHERE as data
	// (neither the utility layer nor any instance), homed in the utility world. `KindName` falls
	// back to `MethodologyPresets.ParseKind` for an undeclared slug regardless of which runtime
	// resolved it, so this shape never actually exercised the bug — it pins that the fix's
	// per-board runtime resolution does not regress the already-working preset case.
	[Fact]
	public async Task Search_BoardScopedKind_ResolvesPresetKind_OnUtilityHomedPresetOnlyBoard()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "quartet");
		var board = await _tasks.CreateBoardAsync(Proj, "scratch", "simple", null, null, TaskBoardMeta.UtilityWorld);
		board.Kind.Should().Be("simple");

		await _tasks.UpsertAsync(Proj, "scratch",
		[
			new NodePatch { Key = "n1", Title = "Note", Body = "x" },
		]);

		var res = await _tasks.SearchNodesAsync(Proj, new SearchRequest<TaskNodeFilter, TaskSortBy>
		{
			Filter = new TaskNodeFilter(Board: "scratch"),
		});
		res.Kind.Should().Be("simple");
	}

	// Sibling defect found while fixing the search one above (same root cause, a different
	// caller of the single project-level runtime): ValidateWiredBoardAsync — the gate behind
	// both tasks_board_create's wiredBoard argument and tasks_board_set_wire — resolved
	// AutoWireFrom against RuntimeAsync (the active instance) instead of the WIRING board's own
	// world, so a work-like kind whose AutoWireFrom is declared only in the utility layer (or a
	// non-active instance) was rejected with "wiredBoard applies only to a work board" even
	// though its own world says otherwise. Covers BOTH call sites: CreateBoardAsync's inline
	// wiredBoard and SetWiredBoardAsync's post-hoc one.
	[Fact]
	public async Task WiredBoardValidation_ResolvesAutoWireFrom_ForKindDeclaredOnlyInUtilityLayer()
	{
		var def = new MethodologyDefinition("utility",
		[
			new MethodologyKindDef("myspec", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["spec-item"],
				[
					new WorkflowStatus("defined", "Defined", StatusKind.Open),
				], []),
			]),
			new MethodologyKindDef("mywork", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["task"],
				[
					new WorkflowStatus("todo", "Todo", StatusKind.Open),
				], []),
			])
			{
				AutoWireFrom = "myspec",
			},
		]);
		await _tasks.DefineMethodologyAsync(Proj, def, 0);

		// The active instance is "classic" — it knows neither "myspec" nor "mywork".
		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "classic");
		var specBoard = await _tasks.CreateBoardAsync(Proj, "myspec-board", "myspec", null, null, TaskBoardMeta.UtilityWorld);

		// First call site: CreateBoardAsync's own inline wiredBoard validation.
		var workBoard = await _tasks.CreateBoardAsync(Proj, "mywork-board", "mywork", null, specBoard.Name, TaskBoardMeta.UtilityWorld);
		workBoard.WiredBoard.Should().Be(specBoard.Name);

		// Second call site: SetWiredBoardAsync, on an already-existing board.
		var otherWork = await _tasks.CreateBoardAsync(Proj, "mywork-board-2", "mywork", null, null, TaskBoardMeta.UtilityWorld);
		var (set, wired) = await _tasks.SetWiredBoardAsync(Proj, otherWork.Name, specBoard.Name);
		set.Should().BeTrue();
		wired.Should().Be(specBoard.Name);
	}
}
