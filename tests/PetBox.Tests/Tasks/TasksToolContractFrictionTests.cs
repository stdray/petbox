using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Mcp;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Tests.Tasks;

// client-issues/tasks-tool-contract-friction-tas-c31570 — three contract mismatches an agent
// onboarding from scratch hit in one session. Each was verified against production before being
// touched; this suite pins the outcome of each verdict so none of the three can silently return.
//
// (1) tasks_upsert REJECTS a node without `key` while the JSON schema types it ["string","null"]
//     and omits it from `required`. CONFIRMED. Fixed as PROSE, not behavior: the schema cannot
//     mark it required without breaking the still-accepted legacy alias `l1`, and making `key`
//     schema-required would fail a currently-valid `l1` call at a strict client's validator. The
//     description now states the requirement and names the alias as the reason.
// (2) tasks_node_get takes `node`, tasks_upsert takes `key`. CONFIRMED as a real naming
//     difference, DECLINED as a rename or alias: `node` is a REFERENCE (slug OR 32-hex NodeId),
//     `key` is the slug FIELD a write sets. Calling both `key` would promise slug-only addressing.
//     Fixed as prose that explains the distinction instead of erasing it.
// (3) tasks_search omits `commits`. CONFIRMED but ONLY in `q` mode — a listing always carried
//     them (which is why it looked unreproducible). `commits` had been swept into the
//     search-lean-rows cut; it is now exempt, because `commit` is a filter on this same tool that
//     applies in BOTH modes. The lean cut itself stays in force for the rest.
public sealed class TasksToolContractFrictionFixture : IDisposable
{
	public const string Proj = "proj";

	readonly string _dir;
	public PetBoxDb Db { get; }
	public ScopedDbFactory<TasksDb> Factory { get; }

	public TasksToolContractFrictionFixture()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-contractfriction-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		Db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		Db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		Factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
	}

	public void Reset()
	{
		Db.TaskBoards.Where(b => b.ProjectKey == Proj).Delete();
		using var tasks = Factory.NewEnsuredConnection(Proj);
		TestDataReset.WipeAllTables(tasks);
	}

	public void Dispose()
	{
		Db.Dispose();
		Factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}
}

public sealed class TasksToolContractFrictionTests : IClassFixture<TasksToolContractFrictionFixture>
{
	const string Proj = TasksToolContractFrictionFixture.Proj;
	const string Sha = "65e9c51b52df4a1c9f0b3d7e8a2c4f6b1d0e9a83";

	readonly TasksService _tasks;

	public TasksToolContractFrictionTests(TasksToolContractFrictionFixture fx)
	{
		fx.Reset();
		_tasks = new TasksService(new TaskBoardStore(fx.Db.Factory(), fx.Factory),
			new RelationStore(fx.Factory), new TagStore(fx.Factory), new CommentService(fx.Factory));
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

	async Task Seed(string board, string nodesJson)
	{
		if (!await _tasks.BoardExistsAsync(Proj, board))
			await _tasks.CreateBoardAsync(Proj, board, null, null, null);
		await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, board, McpInputs.NodesJson(nodesJson));
	}

	Task<TaskSearchResultView> Search(string? q = null, string? board = null, string? commit = null, int? bodyLen = null) =>
		TasksTools.SearchAsync(Http(), Flags(), _tasks, Proj, q, board, null, null, null,
			null, null, bodyLen, null, false, commit, null, null);

	// A node that carries commits plus one that does not — the production shape the reporter saw
	// (some rows `[]`, some populated).
	Task SeedCommitBoard() => Seed("b", $$"""
		[{"key":"alpha-carrier","status":"Todo","title":"Alpha carrier","body":"alpha body","priority":30,"commits":["{{Sha}}"]},
		 {"key":"alpha-bare","status":"Todo","title":"Alpha bare","body":"alpha body","priority":10}]
		""");

	// ── (3) commits ride BOTH modes ──────────────────────────────────────────────────────────

	// The regression itself. A LISTING always carried commits (the counter-evidence that made the
	// report look unreproducible); a QUERY dropped them. Both are asserted here so the next reader
	// can see that the two modes were only ever different in this one field.
	[Fact]
	public async Task Commits_RideQueryRows_NotJustListingRows()
	{
		await SeedCommitBoard();

		var listed = await Search(board: "b", bodyLen: 0);
		listed.Nodes.Single(n => n.Key == "alpha-carrier").Commits.Should().BeEquivalentTo([Sha]);
		listed.Nodes.Single(n => n.Key == "alpha-bare").Commits.Should().BeEmpty();

		var queried = await Search(q: "alpha", board: "b", bodyLen: 0);
		queried.Nodes.Should().NotBeEmpty("the lexical leg must match the seeded bodies");
		queried.Nodes.Single(n => n.Key == "alpha-carrier").Commits.Should().BeEquivalentTo([Sha],
			"a query row must show the commits a listing row shows — a second tasks_node_get per row "
			+ "was the whole friction reported in client-issues/tasks-tool-contract-friction-tas-c31570");
		queried.Nodes.Single(n => n.Key == "alpha-bare").Commits.Should().BeEmpty(
			"an empty set is a fact about the node, not a reason to omit the field");
	}

	// The motivating asymmetry: `commit` filters in BOTH modes, so a query that SELECTS on commits
	// must not hide them. This is the argument that carves commits out of search-lean-rows.
	[Fact]
	public async Task CommitFilter_WithQuery_ShowsTheCommitItMatchedOn()
	{
		await SeedCommitBoard();

		var byPrefix = await Search(q: "alpha", board: "b", commit: "65e9c51", bodyLen: 0);

		byPrefix.Nodes.Select(n => n.Key).Should().Equal("alpha-carrier");
		byPrefix.Nodes.Single().Commits.Should().BeEquivalentTo([Sha]);
	}

	// Guard the other direction: exempting commits must NOT have quietly un-leaned the whole row.
	// spec search-lean-rows still governs the rest of the enrichment.
	[Fact]
	public async Task LeanCut_StillDropsTheRestOfTheEnrichment_InQueryMode()
	{
		await SeedCommitBoard();

		var queried = (await Search(q: "alpha", board: "b", bodyLen: 0)).Nodes.Single(n => n.Key == "alpha-carrier");
		queried.Priority.Should().BeNull("priority stays lean-cut");
		queried.Depth.Should().BeNull("parent/depth stay lean-cut");

		var listed = (await Search(board: "b", bodyLen: 0)).Nodes.Single(n => n.Key == "alpha-carrier");
		listed.Priority.Should().Be(30, "a listing row keeps the full enrichment");
	}

	// ── (1) tasks_upsert: `key` is required, and the description admits it ───────────────────

	[Fact]
	public async Task Upsert_WithoutKey_IsRejected_WithTheDomainMessage()
	{
		await Seed("b", """[{"key":"seed","status":"Todo","title":"Seed","body":"x"}]""");

		var act = async () => await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson("""[{"title":"no key here"}]"""));

		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*each node needs a 'key'*");
	}

	// INVERTED by drop-legacy-aliases (was Upsert_WithLegacyL1Alias_StillLandsAsTheKey). This test
	// used to defend the alias precisely because it was the REASON `key` could not be marked
	// required. The owner took the breaking change instead, so the schema change IS the fix now and
	// the alias must NOT land: `l1` no longer binds, the node has no key, the write is refused.
	[Fact]
	public async Task Upsert_WithLegacyL1Alias_NoLongerLands()
	{
		await Seed("b", """[{"key":"seed","status":"Todo","title":"Seed","body":"x"}]""");

		var act = async () => await TasksTools.UpsertAsync(Http(), Flags(), _tasks, Proj, "b",
			McpInputs.NodesJson("""[{"l1":"via-alias","status":"Todo","title":"Via alias","body":"x"}]"""));

		(await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*each node needs a 'key'*");
	}

	// ── prose gates: the description is what an agent reads INSTEAD of documentation ─────────

	// The prose half of the same inversion. 69e09e00 fixed the key contract as WORDS because the live
	// `l1` alias blocked the schema fix; drop-legacy-aliases removed the alias, so the apology
	// ("back-compat artifact, not optionality") became false and had to go. What the description must
	// say now is that the schema itself carries the requirement — and it must NOT re-acquire an
	// excuse, which is what the negative assertion guards.
	// Tool descriptions are hard-wrapped, so any assertion long enough to be meaningful will cross a
	// line break. Collapse runs of whitespace first and match on the sentence, not on the wrapping.
	static string Flat(string? text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ");

	[Fact]
	public void UpsertDescription_SaysKeyIsRequired_AndClaimsTheSchemaSaysSo()
	{
		var full = Flat(McpToolDescriptions.Full(RegisteredDescription("tasks_upsert")));

		full.Should().Contain("`key` is REQUIRED on EVERY node");
		full.Should().Contain("`key` IS listed in the node object's `required`");
		full.Should().NotContain("back-compat artifact, not optionality",
			"the alias that forced that caveat is gone — the schema now carries the marker honestly");
	}

	// The node-vs-key distinction 69e09e00 established as prose, now stated in the SAME words on both
	// sides: a reference takes either form, `key` takes the slug only. Task 3 of drop-legacy-aliases
	// made this phrasing uniform across every reference parameter, so it is pinned rather than left
	// to drift back into three spellings ("slug|NodeId", "slug key … or its 32-hex NodeId", silence).
	[Fact]
	public void ReferenceParameters_ShareOneFormulation()
	{
		foreach (var tool in new[] { "tasks_search", "tasks_node_get", "tasks_upsert", "relations_create", "relations_list", "comments_search", "comments_upsert" })
			Flat(McpToolDescriptions.Full(RegisteredDescription(tool)))
				.Should().Contain("both accepted", $"{tool} must use the shared node-reference formulation");

		// And the exception stays explicit rather than inferable.
		Flat(McpToolDescriptions.Full(RegisteredDescription("tasks_upsert")))
			.Should().Contain("it never takes a NodeId");
	}

	[Fact]
	public void NodeGetDescription_ExplainsWhyItIsNodeAndNotKey()
	{
		var full = McpToolDescriptions.Full(RegisteredDescription("tasks_node_get"))!;

		full.Should().Contain("The parameter is `node`, NOT `key`");
		full.Should().Contain("you WRITE a `key`, you READ BY a `node`");
	}

	// tasks_search must state the per-mode row shape in words — the head/full text is the only
	// place a caller can learn that a `q` row is not a listing row.
	[Fact]
	public void SearchDescription_SaysCommitsRideBothModes_AndNamesWhatTheLeanCutDrops()
	{
		var full = McpToolDescriptions.Full(RegisteredDescription("tasks_search"))!;

		full.Should().Contain("`commits` is EXEMPT from the lean cut");
		full.Should().Contain("links/delivery/parent/renamedFrom/priority are dropped");
		full.Should().NotContain("commits/priority are dropped",
			"the old sentence claimed commits were dropped in query mode");
	}

	// The registered [Description] essay for a tool, by its McpServerTool name (mirrors
	// WriteVerbOmissionProseTests.RegisteredDescription).
	static string RegisteredDescription(string toolName)
	{
		foreach (var type in typeof(ModuleMcp).Assembly.GetTypes())
			foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
				if (m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName)
					return m.GetCustomAttribute<DescriptionAttribute>()?.Description
						?? throw new InvalidOperationException($"{toolName} has no [Description]");
		throw new InvalidOperationException($"no MCP tool named '{toolName}'");
	}
}
