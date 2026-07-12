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

// methodology-instance-core: named instance entity + board membership + create/list/get/close
// + adopt/move. Acceptance plays from the work card.
public sealed class MethodologyInstanceTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public MethodologyInstanceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-minst-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
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
	public async Task Create_BuiltinQuartet_BoardsExist_NodesWritable()
	{
		var ack = await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "quartet");
		ack.Changed.Should().BeTrue();
		ack.Closed.Should().BeFalse();
		ack.Boards.Should().HaveCount(4);
		ack.Boards.Select(b => b.Kind).Should().BeEquivalentTo(["intake", "ideas", "spec", "work"]);
		ack.Boards.Should().OnlyContain(b => !b.Closed);

		// Membership set on every board.
		var boards = await _tasks.ListBoardsAsync(Proj);
		boards.Should().HaveCount(4);
		boards.Should().OnlyContain(b => b.MethodologyInstance == "main");

		// work→spec auto-wired within the instance.
		var work = boards.Single(b => b.Kind == "work");
		work.SpecBoard.Should().Be(boards.Single(b => b.Kind == "spec").Name);

		// Nodes writable on a provisioned board.
		var ideaBoard = boards.Single(b => b.Kind == "ideas").Name;
		var outcome = await _tasks.UpsertAsync(Proj, ideaBoard,
		[
			new NodePatch { Key = "idea-1", Title = "Explore", Type = "idea", Status = "raw", Body = "x" },
		]);
		outcome.Result.Applied.Should().BeTrue();

		var view = await _tasks.GetMethodologyInstanceAsync(Proj, "main");
		view.Should().NotBeNull();
		view!.Name.Should().Be("main");
		view.Closed.Should().BeFalse();
		view.Kinds.Should().BeEquivalentTo(["intake", "ideas", "spec", "work"]);
		view.Boards.Should().HaveCount(4);
	}

	[Fact]
	public async Task Create_BuiltinClassic_ThenSecondInstance_OverlappingProcessRolesAllowed()
	{
		var a = await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "builtin", "classic");
		a.Boards.Should().HaveCount(1);
		a.Boards[0].Kind.Should().Be("classic");

		// Second instance with overlapping process-role kinds (quartet) is allowed —
		// singleton is per-instance, not project-wide.
		var b = await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "builtin", "quartet");
		b.Boards.Should().HaveCount(4);

		// Both instances listed.
		var list = await _tasks.ListMethodologyInstancesAsync(Proj);
		list.Select(i => i.Name).Should().BeEquivalentTo(["alpha", "beta"]);

		// Process-role singleton INSIDE beta: cannot add a second open work board to beta.
		var workName = b.Boards.Single(x => x.Kind == "work").Name;
		var dup = () => _tasks.CreateBoardAsync(Proj, "extra-work", "work", null, null, "beta");
		(await dup.Should().ThrowAsync<ArgumentException>()).WithMessage("*beta*work*");

		// But a work board on a third instance is fine.
		var gamma = await _tasks.CreateMethodologyInstanceAsync(Proj, "gamma", "builtin", "classic");
		gamma.Boards.Should().HaveCount(1);

		// Creating a work board on alpha (classic instance has no work yet) is fine.
		var workOnAlpha = await _tasks.CreateBoardAsync(Proj, "alpha-work", "work", null, null, "alpha");
		workOnAlpha.MethodologyInstance.Should().Be("alpha");
		workOnAlpha.Name.Should().NotBe(workName);
	}

	[Fact]
	public async Task Close_ClosesBoards_Readable_NoNewWork()
	{
		var ack = await _tasks.CreateMethodologyInstanceAsync(Proj, "flow", "builtin", "quartet");
		var ideas = ack.Boards.Single(b => b.Kind == "ideas").Name;
		await _tasks.UpsertAsync(Proj, ideas,
		[
			new NodePatch { Key = "keep-me", Title = "History", Type = "idea", Status = "raw", Body = "x" },
		]);

		var closed = await _tasks.CloseMethodologyInstanceAsync(Proj, "flow");
		closed.Changed.Should().BeTrue();
		closed.Closed.Should().BeTrue();
		closed.Boards.Should().OnlyContain(b => b.Closed);

		var view = await _tasks.GetMethodologyInstanceAsync(Proj, "flow");
		view!.Closed.Should().BeTrue();
		view.ClosedAt.Should().NotBeNull();

		// History readable.
		var nodes = await _tasks.GetAsync(Proj, ideas, includeClosed: false);
		nodes.Nodes.Should().Contain(n => n.Key == "keep-me");

		// No new work.
		var write = () => _tasks.UpsertAsync(Proj, ideas,
		[
			new NodePatch { Key = "nope", Title = "Blocked", Type = "idea", Status = "raw", Body = "x" },
		]);
		(await write.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*closed*");

		// Idempotent re-close.
		var again = await _tasks.CloseMethodologyInstanceAsync(Proj, "flow");
		again.Changed.Should().BeFalse();
		again.Closed.Should().BeTrue();
	}

	[Fact]
	public async Task BoardCreate_WithoutInstance_Rejected_OnceInstancesExist()
	{
		// Pre-instance world: board_create without membership still works (legacy/tests).
		var legacy = await _tasks.CreateBoardAsync(Proj, "scratch", "simple", null, null);
		legacy.MethodologyInstance.Should().BeNull();

		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "classic");

		// Once any instance exists, board_create without instance is rejected.
		var act = () => _tasks.CreateBoardAsync(Proj, "orphan", "simple", null, null);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*methodology instance is required*");

		// With instance: ok.
		var ok = await _tasks.CreateBoardAsync(Proj, "owned", "simple", null, null, "main");
		ok.MethodologyInstance.Should().Be("main");
	}

	[Fact]
	public async Task Adopt_MoveBoardBetweenInstances()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "a", "builtin", "classic");
		await _tasks.CreateMethodologyInstanceAsync(Proj, "b", "builtin", "classic");

		var board = await _tasks.CreateBoardAsync(Proj, "movable", "simple", null, null, "a");
		board.MethodologyInstance.Should().Be("a");

		var moved = await _tasks.AdoptBoardAsync(Proj, "movable", "b");
		moved.MethodologyInstance.Should().Be("b");

		// Idempotent adopt into same instance.
		var again = await _tasks.AdoptBoardAsync(Proj, "movable", "b");
		again.MethodologyInstance.Should().Be("b");

		// Process-role singleton blocks adopt of a second work board into an instance that has one.
		var workA = await _tasks.CreateBoardAsync(Proj, "wa", "work", null, null, "a");
		await _tasks.CreateBoardAsync(Proj, "wb", "work", null, null, "b");
		var clash = () => _tasks.AdoptBoardAsync(Proj, workA.Name, "b");
		(await clash.Should().ThrowAsync<ArgumentException>()).WithMessage("*work*");
	}

	[Fact]
	public async Task Create_FromTemplate_And_FromInstanceSnapshot()
	{
		// Store a tiny template, then create an instance from it.
		var def = new MethodologyDefinition("tiny",
		[
			new MethodologyKindDef("simple", QuickAddAllowed: true,
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
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "my-tmpl", def, 0);

		var fromTmpl = await _tasks.CreateMethodologyInstanceAsync(Proj, "from-tmpl", "template", "my-tmpl");
		fromTmpl.Boards.Should().HaveCount(1);
		fromTmpl.Boards[0].Kind.Should().Be("simple");

		// Snapshot instance → template, then create another instance from that instance.
		var snap = await _tasks.SnapshotMethodologyTemplateAsync(Proj, "snap-from-inst", 0, from: "instance:from-tmpl");
		snap.Changed.Should().BeTrue();
		var tmpl = await _tasks.GetMethodologyTemplateAsync(Proj, "snap-from-inst");
		tmpl!.Definition.Name.Should().Be("tiny");

		var fromInst = await _tasks.CreateMethodologyInstanceAsync(Proj, "clone", "instance", "from-tmpl");
		fromInst.Boards.Should().HaveCount(1);
		fromInst.Boards[0].Kind.Should().Be("simple");
	}

	[Fact]
	public async Task Create_RequiresExplicitSource_NoSilentDefault()
	{
		var bad = () => _tasks.CreateMethodologyInstanceAsync(Proj, "x", "nope", "quartet");
		(await bad.Should().ThrowAsync<ArgumentException>()).WithMessage("*builtin|template|instance*");

		var missingKey = () => _tasks.CreateMethodologyInstanceAsync(Proj, "y", "builtin", "");
		(await missingKey.Should().ThrowAsync<ArgumentException>()).WithMessage("*sourceKey*");
	}

	[Fact]
	public async Task Enable_Compat_CreatesInstanceNamedAfterPreset()
	{
		var result = await _tasks.EnableMethodologyAsync(Proj, "quartet");
		result.Preset.Should().Be("quartet");
		result.Boards.Should().HaveCount(4);

		var inst = await _tasks.GetMethodologyInstanceAsync(Proj, "quartet");
		inst.Should().NotBeNull();
		inst!.Boards.Should().HaveCount(4);

		// Idempotent rerun.
		var again = await _tasks.EnableMethodologyAsync(Proj, "quartet");
		again.Boards.Should().OnlyContain(b => !b.Created);
	}

	// methodology-instance-scoped-axes: tagAxes + declared linkKinds live on the instance
	// rules document (via board membership), not as project-global authority. Two instances
	// with different dictionaries must not leak into each other.
	[Fact]
	public async Task AxesAndLinkKinds_Isolated_BetweenTwoInstances()
	{
		static MethodologyDefinition Def(string name, string axis, string linkKind) => new(name,
		[
			new MethodologyKindDef("simple", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(
					["task"],
					[
						new WorkflowStatus("Todo", "Todo", StatusKind.Open),
						new WorkflowStatus("Done", "Done", StatusKind.TerminalOk),
					],
					[new MethodologyTransitionDef("Todo", "Done")]),
			]),
		])
		{
			TagAxes = [new MethodologyTagAxisDef(axis)],
			LinkKinds = [new MethodologyLinkKindDef(linkKind, $"{linkKind} edge")],
		};

		await _tasks.UpsertMethodologyTemplateAsync(Proj, "tmpl-a", Def("proc-a", "severity", "escalates"), 0);
		await _tasks.UpsertMethodologyTemplateAsync(Proj, "tmpl-b", Def("proc-b", "channel", "handoff"), 0);
		var instA = await _tasks.CreateMethodologyInstanceAsync(Proj, "alpha", "template", "tmpl-a");
		var instB = await _tasks.CreateMethodologyInstanceAsync(Proj, "beta", "template", "tmpl-b");
		var boardA = instA.Boards.Single().Name;
		var boardB = instB.Boards.Single().Name;

		// Tag axes: each instance enforces only its own namespace.
		var okA = await _tasks.UpsertAsync(Proj, boardA,
		[
			new NodePatch { Key = "a1", Title = "A1", Type = "task", Status = "Todo", Body = "x",
				Tags = ["severity:high"] },
			new NodePatch { Key = "a2", Title = "A2", Type = "task", Status = "Todo", Body = "x",
				Tags = ["severity:low"] },
		]);
		okA.Result.Applied.Should().BeTrue();

		var leakA = () => _tasks.UpsertAsync(Proj, boardA,
		[
			new NodePatch { Key = "a-leak", Title = "Leak", Type = "task", Status = "Todo", Body = "x",
				Tags = ["channel:email"] },
		]);
		(await leakA.Should().ThrowAsync<ArgumentException>()).WithMessage("*channel*");

		var okB = await _tasks.UpsertAsync(Proj, boardB,
		[
			new NodePatch { Key = "b1", Title = "B1", Type = "task", Status = "Todo", Body = "x",
				Tags = ["channel:email"] },
			new NodePatch { Key = "b2", Title = "B2", Type = "task", Status = "Todo", Body = "x",
				Tags = ["channel:chat"] },
		]);
		okB.Result.Applied.Should().BeTrue();

		var leakB = () => _tasks.UpsertAsync(Proj, boardB,
		[
			new NodePatch { Key = "b-leak", Title = "Leak", Type = "task", Status = "Todo", Body = "x",
				Tags = ["severity:high"] },
		]);
		(await leakB.Should().ThrowAsync<ArgumentException>()).WithMessage("*severity*");

		// Link kinds: declared vocabulary is the FROM node's instance.
		var a1 = (await _tasks.GetAsync(Proj, boardA)).Nodes.Single(n => n.Key == "a1");
		var a2 = (await _tasks.GetAsync(Proj, boardA)).Nodes.Single(n => n.Key == "a2");
		var b1 = (await _tasks.GetAsync(Proj, boardB)).Nodes.Single(n => n.Key == "b1");
		var b2 = (await _tasks.GetAsync(Proj, boardB)).Nodes.Single(n => n.Key == "b2");

		(await _tasks.ValidateRelationKindAsync(Proj, "escalates", a1.NodeId)).Should().Be("escalates");
		(await _tasks.ValidateRelationKindAsync(Proj, "handoff", b1.NodeId)).Should().Be("handoff");

		var crossA = () => _tasks.ValidateRelationKindAsync(Proj, "handoff", a1.NodeId);
		(await crossA.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*handoff*")
			.WithMessage("*alpha*");

		var crossB = () => _tasks.ValidateRelationKindAsync(Proj, "escalates", b1.NodeId);
		(await crossB.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*escalates*")
			.WithMessage("*beta*");

		// Builtins remain available on every instance.
		(await _tasks.ValidateRelationKindAsync(Proj, "relates_to", a1.NodeId)).Should().Be("relates_to");
		(await _tasks.ValidateRelationKindAsync(Proj, "blocks", b1.NodeId)).Should().Be("blocks");

		// MCP relations_create follows the same instance scope (from-node).
		var http = Http("tasks:read tasks:write");
		var flags = Flags();
		var relations = new RelationStore(_factory);

		var created = await RelationTools.CreateAsync(http, flags, relations, _tasks, Proj, kind: "escalates", fromNodeId: a1.NodeId, toNodeId: a2.NodeId);
		created.Relations.Should().ContainSingle();
		created.Relations[0].Kind.Should().Be("escalates");

		var mcpCross = () => RelationTools.CreateAsync(http, flags, relations, _tasks, Proj, kind: "escalates", fromNodeId: b1.NodeId, toNodeId: b2.NodeId);
		(await mcpCross.Should().ThrowAsync<ArgumentException>()).WithMessage("*escalates*");
	}

	// Drop dual-read: methodology_defs no longer feed Runtime/tag axes for unassigned boards.
	// With no open instances, unassigned boards resolve presets only (simple = free-form tags).
	[Fact]
	public async Task LegacyUnassignedBoard_IgnoresProjectSingletonAxes()
	{
		// Defs may still be written for history, but must not drive live process resolution.
		await _tasks.DefineMethodologyAsync(Proj, new MethodologyDefinition("legacy",
		[
			new MethodologyKindDef("simple", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(
					["task"],
					[
						new WorkflowStatus("Todo", "Todo", StatusKind.Open),
						new WorkflowStatus("Done", "Done", StatusKind.TerminalOk),
					],
					[new MethodologyTransitionDef("Todo", "Done")]),
			]),
		])
		{
			TagAxes = [new MethodologyTagAxisDef("legacyaxis")],
			LinkKinds = [new MethodologyLinkKindDef("legacyedge")],
		}, 0);

		var board = await _tasks.CreateBoardAsync(Proj, "orphan", "simple", null, null);
		board.MethodologyInstance.Should().BeNull();

		// Free-form tags on simple (presets) — def axes are ignored.
		var ok = await _tasks.UpsertAsync(Proj, board.Name,
		[
			new NodePatch { Key = "n1", Title = "N1", Type = "task", Status = "Todo", Body = "x",
				Tags = ["severity:high"] },
		]);
		ok.Result.Applied.Should().BeTrue();

		var n1 = (await _tasks.GetAsync(Proj, board.Name)).Nodes.Single(n => n.Key == "n1");
		var legacyEdge = () => _tasks.ValidateRelationKindAsync(Proj, "legacyedge", n1.NodeId);
		(await legacyEdge.Should().ThrowAsync<ArgumentException>()).WithMessage("*legacyedge*",
			"def-declared link kinds are not live without an open instance");
	}

	[Fact]
	public async Task Mcp_CreateListGetClose_RoundTrip()
	{
		var http = Http("tasks:read tasks:write");
		var flags = Flags();

		var created = await TasksTools.MethodologyCreateAsync(http, flags, _tasks, Proj, "mcp-main", "builtin", "classic");
		created.Name.Should().Be("mcp-main");
		created.Boards.Should().HaveCount(1);

		var list = await TasksTools.MethodologyListAsync(http, flags, _tasks, Proj);
		list.Instances.Should().Contain(i => i.Name == "mcp-main");

		var get = await TasksTools.MethodologyGetAsync(http, flags, _tasks, Proj, "mcp-main");
		get.Found.Should().BeTrue();
		get.Instance!.Name.Should().Be("mcp-main");

		var miss = await TasksTools.MethodologyGetAsync(http, flags, _tasks, Proj, "nope");
		miss.Found.Should().BeFalse();

		var closed = await TasksTools.MethodologyCloseAsync(http, flags, _tasks, Proj, "mcp-main");
		closed.Closed.Should().BeTrue();
		closed.Boards.Should().OnlyContain(b => b.Closed);
	}
}
