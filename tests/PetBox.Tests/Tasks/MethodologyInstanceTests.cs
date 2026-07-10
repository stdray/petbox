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
		_tasks = new TasksService(new TaskBoardStore(_db, _factory), new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
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
		var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(id) };
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

		var get = await TasksTools.MethodologyInstanceGetAsync(http, flags, _tasks, Proj, "mcp-main");
		get.Found.Should().BeTrue();
		get.Instance!.Name.Should().Be("mcp-main");

		var miss = await TasksTools.MethodologyInstanceGetAsync(http, flags, _tasks, Proj, "nope");
		miss.Found.Should().BeFalse();

		var closed = await TasksTools.MethodologyCloseAsync(http, flags, _tasks, Proj, "mcp-main");
		closed.Closed.Should().BeTrue();
		closed.Boards.Should().OnlyContain(b => b.Closed);
	}
}
