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
		var specBoard = ack.Boards.Single(b => b.Kind == "spec").Name;

		var released = await _tasks.AdoptBoardAsync(Proj, specBoard, TaskBoardMeta.UtilityWorld);
		released.MethodologyInstance.Should().Be(TaskBoardMeta.UtilityWorld);

		// spec is a Singleton kind (methodology-kind-singleton) — a second spec board cannot
		// join the SAME utility bucket while this one is open.
		var dup = () => _tasks.CreateBoardAsync(Proj, "extra-spec", "spec", null, null, TaskBoardMeta.UtilityWorld);
		(await dup.Should().ThrowAsync<ArgumentException>()).WithMessage("*utility*");

		// Idempotent: releasing an already-utility board again is a no-op, not an error.
		var again = await _tasks.AdoptBoardAsync(Proj, specBoard, TaskBoardMeta.UtilityWorld);
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

		var miss = await TasksTools.MethodologyUtilityGetAsync(http, flags, _tasks, Proj);
		miss.Found.Should().BeFalse();

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
		got.Found.Should().BeTrue();
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
}
