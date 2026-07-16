using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// tasks_search response budget (spec bounded-result-sets, generalizing the methodology_get
// pattern via the shared ResponseBudget helper): a board too large for one response is
// prefix-cut on the wire form of its rows and marked structurally (truncated/omitted +
// a narrowing hint) — never silently; a board that fits serializes exactly as before
// (the marker fields default to null and the wire serializer omits nulls).
public sealed class TasksGetBudgetTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public TasksGetBudgetTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-getbudget-" + Guid.NewGuid().ToString("N"));
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

	static IHttpContextAccessor Http()
	{
		var id = new ClaimsIdentity([new Claim("project", Proj), new Claim("scopes", "tasks:read,tasks:write")], "test");
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

	// The MCP wire shape (camelCase + null-omit) — what an agent actually receives.
	static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	// Seed `count` nodes, each with a `bodyChars`-char body.
	async Task SeedAsync(string board, int count, int bodyChars)
	{
		// The tool layer no longer auto-vivifies a board (namespace-creation gate); create it
		// explicitly first, as the old cold-upsert auto-vivify did (a simple board).
		if (!await _tasks.BoardExistsAsync(Proj, board))
			await _tasks.CreateBoardAsync(Proj, board, null, null, null);
		var body = new string('b', bodyChars);
		var rows = string.Join(",", Enumerable.Range(0, count).Select(i =>
			$$"""{"key":"node-{{i:d3}}","status":"Todo","title":"Node {{i}}","body":"{{body}}"}"""));
		await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, board, McpInputs.NodesJson($"[{rows}]"));
	}

	[Fact]
	public async Task SmallBoard_NoMarkers_WireShapeUnchanged()
	{
		await SeedAsync("small", 3, 200);

		var view = await TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, board: "small");

		view.Nodes.Count.Should().Be(3);
		view.Truncated.Should().BeNull();
		view.Omitted.Should().BeNull();
		view.Hint.Should().BeNull();
		// Byte-for-byte: the marker fields are null → the wire serializer omits them entirely.
		var json = JsonSerializer.Serialize(view, Wire);
		json.Should().NotContainAny("truncated", "omitted", "hint");
		json.Should().Be(JsonSerializer.Serialize(new TaskSearchResultView(
			view.Nodes, view.Board, view.Kind, view.SpecBoard, view.CurrentVersion), Wire));
	}

	[Fact]
	public async Task LargeBoard_RowsPrefixCut_MarkersAndHint()
	{
		const int total = 60;
		await SeedAsync("big", total, 1000); // ~60k chars of bodies alone > the 30k budget

		// bodyLen:-1 = the full body (the default is now a compact snippet); full bodies overflow.
		var view = await TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, board: "big", bodyLen: -1);

		// Prefix-cut in board order (priority then key), the cut is explicit and adds up.
		view.Nodes.Count.Should().BeGreaterThan(0).And.BeLessThan(total);
		view.Nodes.Select(n => n.Key).Should().Equal(
			Enumerable.Range(0, view.Nodes.Count).Select(i => $"node-{i:d3}"));
		view.Truncated.Should().BeTrue();
		view.Omitted.Should().Be(total - view.Nodes.Count);
		view.Hint.Should().NotBeNull();
		view.Hint.Should().ContainAll("under", "status", "bodyLen", "groupBy", "tasks_node_get");
		// The kept rows still carry their FULL bodies — the budget cuts rows, not content.
		view.Nodes.Should().OnlyContain(n => n.Body!.Length == 1000);
	}

	[Fact]
	public async Task BodyLen_ShrinksRows_SoMoreFitTheBudget()
	{
		const int total = 60;
		await SeedAsync("big", total, 1000);

		var full = await TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, board: "big", bodyLen: -1);
		var snipped = await TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, board: "big", bodyLen: 20);

		// The budget measures the POST-slicing wire rows: snippets are cheap, so the whole
		// board fits and the markers disappear.
		snipped.Nodes.Count.Should().Be(total);
		snipped.Truncated.Should().BeNull();
		snipped.Nodes.Should().OnlyContain(n => n.Body!.Length == 21 && n.Body.EndsWith('…'));
		full.Nodes.Count.Should().BeLessThan(snipped.Nodes.Count);
	}

	[Fact]
	public async Task Under_And_GroupBy_KeepWorkingWithTheBudget()
	{
		const int total = 60;
		await SeedAsync("big", total, 1000);
		// A small subtree: parent + child, tagged for the projection.
		await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, "big", McpInputs.NodesJson(
			"""
			[{"key":"apex","status":"Todo","title":"Apex","body":"tiny","tags":["area:tasks"]},
			 {"key":"apex-leaf","status":"Todo","title":"Leaf","body":"tiny","partOf":"apex"}]
			"""));

		// `under` narrows below the budget → complete answer, no markers.
		var under = await TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, board: "big", under: "apex");
		under.Nodes.Select(n => n.Key).Should().BeEquivalentTo("apex", "apex-leaf");
		under.Truncated.Should().BeNull();
		under.Hint.Should().BeNull();

		// `groupBy` is the keys-only projection — a different (cheap) shape, no budget markers.
		var grouped = await TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, board: "big", groupBy: "area");
		grouped.Groups.Should().NotBeEmpty();
		grouped.Groups!.SelectMany(g => g.NodeKeys).Should().Contain("apex");
		grouped.Truncated.Should().BeNull();
	}

	// The shared helper itself: wire-form cost, prefix-cut, response-wide accumulation.
	[Fact]
	public void ResponseBudget_Take_PrefixCuts_AndAccumulatesAcrossLists()
	{
		var row = new { Name = "abc", Skip = (string?)null }; // null omitted from the cost
		var cost = PetBox.Core.Contract.ResponseBudget.CostOf(row);
		cost.Should().Be("""{"name":"abc"}""".Length); // camelCase, null-omit

		var budget = new PetBox.Core.Contract.ResponseBudget(cost * 3);
		var (first, omitted1) = budget.Take([row, row]);
		first.Count.Should().Be(2);
		omitted1.Should().Be(0);
		// The SAME budget spans the next list (response-wide, like the quartet boards).
		var (second, omitted2) = budget.Take([row, row]);
		second.Count.Should().Be(1);
		omitted2.Should().Be(1);
	}
}
