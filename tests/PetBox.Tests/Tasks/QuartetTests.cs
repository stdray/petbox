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

// methodology-quartet: opt-in provisioning of the four singleton boards + auto-wiring
// work->spec, the one-per-project singleton guard, and the unified surface.
public sealed class QuartetTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public QuartetTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-quartet-" + Guid.NewGuid().ToString("N"));
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

	[Fact]
	public async Task Create_BuiltinQuartet_ProvisionsBoards_AndAutoWires()
	{
		var http = Http("tasks:read,tasks:write");
		var en = await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "quartet", "builtin", "quartet");
		en.Name.Should().Be("quartet");
		en.Changed.Should().BeTrue();
		// Member boards ordered by name (stable index), not pipeline-kind order.
		en.Boards.Select(b => b.Kind)
			.Should().BeEquivalentTo(["intake", "ideas", "spec", "work"]);
		en.Boards.Should().OnlyContain(b => b.Name == b.Kind && !b.Closed);

		// work board auto-wired to the spec board.
		var boards = (await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).Boards;
		var work = boards.Single(b => b.Kind == "work");
		work.SpecBoard.Should().Be("spec");
		work.MethodologyInstance.Should().Be("quartet");

		// Re-create of the same name is rejected (create is not enable-style idempotent).
		var again = () => TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "quartet", "builtin", "quartet");
		(await again.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*already exists*");
		(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).Boards.Count.Should().Be(4);
	}

	// An unknown builtin sourceKey is rejected before any board is created.
	[Fact]
	public async Task Create_UnknownBuiltin_Rejected_ListingSlugs()
	{
		var act = () => _tasks.CreateMethodologyInstanceAsync(Proj, "x", "builtin", "banana");
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*banana*");
	}

	// classic builtin: provisions exactly one standalone classic board — no quartet auto-wire.
	[Fact]
	public async Task Create_BuiltinClassic_ProvisionsOneClassicBoard()
	{
		var http = Http("tasks:read,tasks:write");
		var en = await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "classic", "builtin", "classic");
		en.Name.Should().Be("classic");
		var reported = en.Boards.Should().ContainSingle().Subject;
		reported.Kind.Should().Be("classic");
		reported.Name.Should().Be("classic");

		var boards = (await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).Boards;
		var classic = boards.Should().ContainSingle().Subject;
		classic.Kind.Should().Be("classic");
		classic.SpecBoard.Should().BeNull("classic is outside the spec/work auto-wire");

		// classic is NOT a process-role singleton — more boards may be created on the same instance.
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "another", "classic",
			methodologyInstance: "classic");
		(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).Boards.Count.Should().Be(2);
	}

	// quick-add-stores-default-type: a single-FSM kind (every preset kind but Work) resolves
	// an untyped node to its declared default (first type of the first block) for FSM
	// purposes; this pins that the WRITE materializes the same default into the stored row
	// (and so the UI) instead of persisting an empty string — the store and the resolution
	// now agree. Covers a quartet kind (ideas -> idea) and the classic preset (classic -> task).
	[Theory]
	[InlineData("quartet", "ideas", "idea")]
	[InlineData("classic", "classic", "task")]
	public async Task Upsert_UntypedNode_MaterializesKindDefaultType(string preset, string board, string defaultType)
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, preset, "builtin", preset);
		var nodes = McpInputs.NodesJson("""[{"key":"untyped-a","title":"A"}]"""); // no type, no status
		var res = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, board, nodes);

		res.Applied.Should().BeTrue();
		var added = res.Added.Should().ContainSingle().Subject;
		added.Type.Should().Be(defaultType, "the stored type must match the kind's runtime-resolved default, not \"\"");

		// The persisted row agrees too (not just the write echo).
		var read = await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, board, "untyped-a");
		read.Node.Type.Should().Be(defaultType);
	}

	// Builtin templates are copyable via template_get (source=builtin, version 0) and valid
	// to install as a stored template (MethodologyDefinitionValidator via template_upsert).
	[Fact]
	public async Task MethodologyTemplateGet_Builtin_IsValidCopyableTemplate()
	{
		var http = Http("tasks:read,tasks:write");

		var render = await TasksTools.MethodologyTemplateGetAsync(http, Flags(), _tasks, Proj, "quartet");
		render.Found.Should().BeTrue();
		render.Source.Should().Be("builtin");
		render.Version.Should().Be(0);
		render.Created.Should().BeNull();
		render.Name.Should().Be("quartet");
		render.Kinds!.Select(k => k.Kind).Should().Equal("intake", "ideas", "spec", "work");
		render.TagAxes!.Select(a => a.Namespace).Should().Equal("area", "concern");

		// The template is a VALID document — store a copy under a new key.
		var ack = await _tasks.UpsertMethodologyTemplateAsync(Proj, "quartet-copy", MethodologyPresets.RenderPresetDefinition("quartet"), 0);
		ack.Changed.Should().BeTrue();

		var stored = await TasksTools.MethodologyTemplateGetAsync(http, Flags(), _tasks, Proj, "quartet-copy");
		stored.Found.Should().BeTrue();
		stored.Source.Should().Be("stored");
		stored.Name.Should().Be("quartet");
		stored.Version.Should().BeGreaterThan(0);

		// Unknown key → found:false (not an error).
		var miss = await TasksTools.MethodologyTemplateGetAsync(http, Flags(), _tasks, Proj, "banana");
		miss.Found.Should().BeFalse();
	}

	[Fact]
	public async Task Singleton_SecondBoardOfMethodologyKind_Rejected()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "spec", "spec");
		// A 2nd open process-role board is rejected (one-per-instance; legacy unassigned
		// boards share the null-membership bucket). GuardAsync is not on board_create,
		// so the service throws — assert the message via the service directly.
		var act = () => _tasks.CreateBoardAsync(Proj, "spec2", "spec", null, null);
		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*one-per-instance*");
	}

	[Fact]
	public async Task Singleton_SimpleBoards_Unlimited()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "f1", "simple");
		await TasksTools.BoardCreateAsync(http, Flags(), _tasks, Proj, "f2", "simple");
		(await TasksTools.BoardListAsync(http, Flags(), _tasks, Proj)).Boards
			.Count.Should().Be(2);
	}

	// Quartet compact index remains a service surface (GetMethodologyAsync); MCP
	// tasks_methodology_get is now instance get(name). These tests pin the service index.
	[Fact]
	public async Task QuartetIndex_IsCompact_WithBodyLen_AndBoardFilter()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "quartet", "builtin", "quartet");

		var body = new string('x', 500);
		var nodes = McpInputs.NodesJson(
			$$"""[{"key":"idea-a","status":"raw","type":"idea","title":"A","body":"{{body}}","tags":["area:tasks"]}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas", nodes);

		// Default: an INDEX — the node carries tags/status/title but the body is sliced to null;
		// the board exposes a status histogram.
		var idx = await _tasks.GetMethodologyAsync(Proj);
		var ideas = idx.Boards.Single(b => b.Kind == "ideas");
		ideas.Counts["raw"].Should().Be(1);
		var nodeA = ideas.Nodes.Single(n => n.Key == "idea-a");
		nodeA.Body.Should().BeNull();        // no body by default
		nodeA.Title.Should().Be("A");
		nodeA.Tags.Should().Contain("area:tasks"); // tags ALWAYS

		// Small boards fit the output budget: no truncation markers, no hint — as before.
		idx.Hint.Should().BeNull();
		ideas.Truncated.Should().BeNull();
		ideas.Omitted.Should().BeNull();

		// bodyLen: the first N chars + "…" when cut.
		var sliced = (await _tasks.GetMethodologyAsync(Proj, bodyLen: 300))
			.Boards.Single(b => b.Kind == "ideas")
			.Nodes.Single(n => n.Key == "idea-a")
			.Body!;
		sliced.Length.Should().Be(301);
		sliced.Should().EndWith("…");

		// includeBoards: only the requested quartet boards, in pipeline order.
		var only = await _tasks.GetMethodologyAsync(Proj, includeBoards: ["spec", "ideas"]);
		only.Boards.Select(b => b.Kind)
			.Should().Equal("ideas", "spec");
	}

	// spec bounded-result-sets / surface-economy: a board too large for the index budget is
	// prefix-cut with STRUCTURAL markers (truncated/omitted per board + a narrowing hint on
	// the view); the status histogram stays complete — the overview never lies about totals.
	[Fact]
	public async Task QuartetIndex_LargeBoard_CutsRowsWithMarkers_CountsStayComplete()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "quartet", "builtin", "quartet");

		const int total = 150;
		var title = new string('t', 200);
		var body = new string('b', 100);
		var rows = string.Join(",", Enumerable.Range(0, total).Select(i =>
			$$"""{"key":"idea-{{i}}","status":"raw","type":"idea","title":"{{title}}","body":"{{body}}"}"""));
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas", McpInputs.NodesJson($"[{rows}]"));

		var idx = await _tasks.GetMethodologyAsync(Proj);
		var ideas = idx.Boards.Single(b => b.Kind == "ideas");

		// Histogram is ALWAYS complete — the cheap essence of the overview.
		ideas.Counts["raw"].Should().Be(total);

		// Rows are prefix-cut, and the cut is explicit: truncated + omitted add back up.
		ideas.Nodes.Count.Should().BeGreaterThan(0).And.BeLessThan(total);
		ideas.Truncated.Should().BeTrue();
		ideas.Omitted.Should().Be(total - ideas.Nodes.Count);

		// The response tells the caller how to narrow.
		idx.Hint.Should().NotBeNull();
		idx.Hint.Should().Contain("includeBoards").And.Contain("tasks_search");

		// bodyLen keeps working under the budget: every INCLUDED row carries its slice
		// ("…"-terminated), and fatter rows just mean fewer of them fit.
		var sliced = (await _tasks.GetMethodologyAsync(Proj, bodyLen: 50))
			.Boards.Single(b => b.Kind == "ideas");
		sliced.Truncated.Should().BeTrue();
		sliced.Nodes.Should().OnlyContain(n => n.Body != null && n.Body.Length == 51 && n.Body.EndsWith('…'));
		sliced.Nodes.Count.Should().BeLessThanOrEqualTo(ideas.Nodes.Count);
	}

	[Fact]
	public async Task QuartetIndex_IncludeUrl_AddsAbsolutePermalink()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "quartet", "builtin", "quartet");
		var nodes = McpInputs.NodesJson("""[{"key":"idea-u","status":"raw","type":"idea","title":"U"}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ideas", nodes);

		// off by default: url is null.
		var off = (await _tasks.GetMethodologyAsync(Proj))
			.Boards.Single(b => b.Kind == "ideas")
			.Nodes.Single(n => n.Key == "idea-u");
		off.Url.Should().BeNull();

		// includeUrl via urlPrefix: canonical slug permalink.
		var on = (await _tasks.GetMethodologyAsync(Proj, urlPrefix: $"https://box.test/ui/ws/{Proj}/tasks/"))
			.Boards.Single(b => b.Kind == "ideas")
			.Nodes.Single(n => n.Key == "idea-u");
		on.Url.Should().Be($"https://box.test/ui/ws/{Proj}/tasks/ideas/idea-u");
	}

	[Fact]
	public async Task Upsert_IncludeUrl_ReturnsPermalinkForCreatedNode()
	{
		var http = Http("tasks:read,tasks:write");
		await EnsureBoard("free1");
		var nodes = McpInputs.NodesJson("""[{"key":"a","status":"Todo","title":"A"}]""");
		var added = (await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "free1", nodes, includeUrl: true))
			.Added.Single();
		added.Url.Should().Be($"https://box.test/ui/ws/{Proj}/tasks/free1/a");
	}

	[Fact]
	public async Task QuartetIndex_InvalidIncludeBoards_SilentlyDropped()
	{
		var http = Http("tasks:read,tasks:write");
		await TasksTools.MethodologyCreateAsync(http, Flags(), _tasks, Proj, "quartet", "builtin", "quartet");
		// An unknown board kind is silently dropped (soft filter); an all-unknown set → no boards.
		var res = await _tasks.GetMethodologyAsync(Proj, includeBoards: ["bogus"]);
		res.Boards.Should().BeEmpty();
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

	// The tool layer no longer auto-vivifies a board (namespace-creation gate). Create it
	// explicitly first, as the old cold-upsert auto-vivify did (a simple board).
	async Task EnsureBoard(string board)
	{
		if (!await _tasks.BoardExistsAsync(Proj, board))
			await _tasks.CreateBoardAsync(Proj, board, null, null, null);
	}

	// spec echo-compact-by-default: the write-echo is a compact ack — it carries
	// key/status/title/version but NOT the body unless bodyLen > 0. Defuses the footgun where
	// a write re-dumps every recently-changed node's full body (the main context sink).
	[Fact]
	public async Task Upsert_EchoOmitsBodyByDefault_SlicesWithBodyLen_AndDeltaStaysBodiless()
	{
		var http = Http("tasks:read,tasks:write");
		var big = new string('y', 500);
		await EnsureBoard("ce");

		// Default echo: title present, body sliced to null (no re-dump of what I just sent).
		var nodesA = McpInputs.NodesJson(
			$$"""[{"key":"a","status":"Todo","title":"A","body":"{{big}}"}]""");
		var resA = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesA);
		var addedA = resA.Added.Single();
		addedA.Title.Should().Be("A");
		addedA.Body.Should().BeNull();

		// A second node echoes only ITSELF (echo-covers-the-call, no cursor parameter on a
		// write); the full board delta is tasks_delta's job — and even that dump stays bodiless.
		var nodesB = McpInputs.NodesJson(
			"""[{"key":"b","status":"Todo","title":"B","body":"zzz"}]""");
		var resB = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesB);
		resB.Added.Concat(resB.Updated).Select(n => n.Key).Should().Equal("b");

		var resFull = await TasksTools.DeltaAsync(http, Flags(), _tasks, Proj, "ce", 0);
		var echoed = resFull.Added.Concat(resFull.Updated).ToList();
		echoed.Select(n => n.Key).Should().Contain(["a", "b"]); // the delta covers everyone
		echoed.Should().OnlyContain(n => n.Body == null);

		// bodyLen > 0: the opt-in sliced body — first N chars + "…" when cut.
		var nodesC = McpInputs.NodesJson(
			$$"""[{"key":"c","status":"Todo","title":"C","body":"{{big}}"}]""");
		var sliced = (await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "ce", nodesC, bodyLen: 300))
			.Added.Single(n => n.Key == "c")
			.Body!;
		sliced.Length.Should().Be(301);
		sliced.Should().EndWith("…");
	}

	// spec bodylen-uniform-contract: tasks_search follows the uniform bodyLen knob — omitted =
	// a ~240-char snippet (compact listing default), -1 = the full body, 0 = no body, N>0 = an
	// N-char snippet. The SAME knob values mean the same thing on every body-carrying surface.
	[Fact]
	public async Task TasksSearch_UniformBodyLenContract()
	{
		var http = Http("tasks:read,tasks:write");
		var big = new string('z', 500);
		await EnsureBoard("g");
		var nodes = McpInputs.NodesJson(
			$$"""[{"key":"n","status":"Todo","title":"N","body":"{{big}}"}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "g", nodes);

		// Default (omitted): a ~240-char snippet + "…".
		var dflt = (await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "g"))
			.Nodes.Single().Body!;
		dflt.Length.Should().Be(241);
		dflt.Should().EndWith("…");

		// -1: the full body.
		var full = (await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "g", bodyLen: -1))
			.Nodes.Single().Body!;
		full.Length.Should().Be(500);

		// 0: no body (null → omitted by the serializer).
		var none = (await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "g", bodyLen: 0))
			.Nodes.Single().Body;
		none.Should().BeNull();

		// N>0: first N chars + "…".
		var snip = (await TasksTools.SearchAsync(http, Flags(), _tasks, Proj, board: "g", bodyLen: 100))
			.Nodes.Single().Body!;
		snip.Length.Should().Be(101);
		snip.Should().EndWith("…");
	}

	// spec node_get bodylen-uniform-contract: tasks_node_get is the pointed full read — omitted
	// = the full body, but the same 0/N/-1 knob still applies.
	[Fact]
	public async Task NodeGet_UniformBodyLenContract()
	{
		var http = Http("tasks:read,tasks:write");
		var big = new string('q', 400);
		await EnsureBoard("g");
		var nodes = McpInputs.NodesJson(
			$$"""[{"key":"n","status":"Todo","title":"N","body":"{{big}}"}]""");
		await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "g", nodes);

		(await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "g", "n")).Node.Body.Length.Should().Be(400); // default = full
		(await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "g", "n", bodyLen: 50)).Node.Body.Length.Should().Be(51); // snippet
		(await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "g", "n", bodyLen: 0)).Node.Body.Should().BeEmpty(); // none
	}

	// spec upsert-ack-echo-clean: a write that did NOT apply echoes NOTHING. A FutureBaseline
	// conflict (a baseline above the board cursor) must leave added/updated/removed empty — the
	// old contract echoed the mentioned node's CURRENT state, reading as if the write landed.
	[Fact]
	public async Task Upsert_NotApplied_EchoIsEmpty_ConflictCarriesTheStory()
	{
		var http = Http("tasks:read,tasks:write");

		// Land a node so the board has a cursor and "a" has current state to (wrongly) echo.
		await EnsureBoard("conf");
		var seed = McpInputs.NodesJson("""[{"key":"a","status":"Todo","title":"A","body":"x"}]""");
		var applied = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "conf", seed);
		applied.Applied.Should().BeTrue();

		// Re-upsert "a" with a baseline ABOVE the board cursor → FutureBaseline conflict.
		var stale = McpInputs.NodesJson(
			"""[{"key":"a","status":"Todo","title":"A2","body":"y","version":9}]""");
		var res = await TasksTools.UpsertAsync(http, Flags(), _tasks, Proj, "conf", stale, bodyLen: -1);

		res.Applied.Should().BeFalse();
		res.Conflicts.Should().NotBeEmpty();
		res.Added.Should().BeEmpty();
		res.Updated.Should().BeEmpty();
		res.Removed.Should().BeEmpty();

		// The node was NOT mutated by the rejected write.
		(await TasksTools.NodeGetAsync(http, Flags(), _tasks, Proj, "conf", "a")).Node.Title.Should().Be("A");
	}
}
