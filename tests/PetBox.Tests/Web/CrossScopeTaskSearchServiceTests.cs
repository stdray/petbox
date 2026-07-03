using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Web.Search;

namespace PetBox.Tests.Web;

// The cross-scope task search fan-out/merge (spec cross-scope-task-search): a single query
// fanned out across every project the caller can reach, merging the identifier fast-path
// (exact slug/NodeId — the FTS index doesn't cover the key, so a bare slug paste needs its
// own leg) with full-text hits. Exercised against REAL TasksService instances over several
// project files sharing one factory root — exactly how multiple projects live in prod (one
// tasks DB file per project key under the same root).
[Collection("DataModule")]
public sealed class CrossScopeTaskSearchServiceTests : IDisposable
{
	const string ProjA = "proj-a"; // lives in ws1
	const string ProjB = "proj-b"; // lives in ws1
	const string ProjC = "proj-c"; // lives in ws2 — used to prove access scoping excludes it

	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TasksService _tasks;

	public CrossScopeTaskSearchServiceTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-xscope-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = ProjA, WorkspaceKey = "ws1", Name = "A", Description = "" });
		_db.Insert(new Project { Key = ProjB, WorkspaceKey = "ws1", Name = "B", Description = "" });
		_db.Insert(new Project { Key = ProjC, WorkspaceKey = "ws2", Name = "C", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		var store = new TaskBoardStore(_db, _factory);
		_tasks = new TasksService(store, new RelationStore(_db), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		SqliteConnection.ClearAllPools();
		if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
	}

	// The dict shape ProjectsByWorkspace hands the service — access scoping lives entirely in
	// what the caller puts here (already filtered to the caller's memberships in production).
	static IReadOnlyDictionary<string, IReadOnlyList<Project>> ByWorkspace(params (string Ws, Project Project)[] entries) =>
		entries.GroupBy(e => e.Ws, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => (IReadOnlyList<Project>)g.Select(e => e.Project).ToList(), StringComparer.Ordinal);

	static Project Proj(string key, string ws) => new() { Key = key, WorkspaceKey = ws, Name = key, Description = "" };

	Task<UpsertResultView> Seed(string project, string board, params NodePatch[] nodes) =>
		Adapt(_tasks.UpsertAsync(project, board, nodes));

	// UpsertAsync returns the raw UpsertOutcome; tests only need Added[].NodeId, so unwrap into
	// a tiny local shape instead of pulling in the MCP adapter's serialization.
	static async Task<UpsertResultView> Adapt(Task<UpsertOutcome> outcome)
	{
		var o = await outcome;
		return new UpsertResultView(o.Result.Added.Select(n => (n.Key, n.NodeId)).ToList());
	}

	sealed record UpsertResultView(IReadOnlyList<(string Key, string NodeId)> Added);

	CrossScopeTaskSearchService CoreOnly() =>
		new(nav: null!, http: null!, tasks: _tasks);

	[Fact]
	public async Task AccessScoping_ExcludesProjectsOutsideTheEnumeration()
	{
		await Seed(ProjC, "work", new NodePatch { Key = "gamma-task", Title = "Gamma", Body = "x" });

		var scope = ByWorkspace((Ws: "ws1", Project: Proj(ProjA, "ws1")), (Ws: "ws1", Project: Proj(ProjB, "ws1")));
		var hits = await CoreOnly().SearchAsync(scope, "gamma-task", "https", "box.test");

		hits.Should().BeEmpty("proj-c is outside the caller's accessible workspaces, so its node must never surface");
	}

	[Fact]
	public async Task IdentifierFastPath_ResolvesExactSlug()
	{
		await Seed(ProjA, "work", new NodePatch { Key = "alpha-task", Title = "Alpha work item", Body = "x" });

		var scope = ByWorkspace((Ws: "ws1", Project: Proj(ProjA, "ws1")), (Ws: "ws1", Project: Proj(ProjB, "ws1")));
		var hits = await CoreOnly().SearchAsync(scope, "alpha-task", "https", "box.test");

		hits.Should().ContainSingle();
		var hit = hits[0];
		hit.ExactMatch.Should().BeTrue();
		hit.Key.Should().Be("alpha-task");
		hit.Workspace.Should().Be("ws1");
		hit.ProjectKey.Should().Be(ProjA);
		hit.Board.Should().Be("work");
		hit.Url.Should().Contain("/ws1/").And.Contain("/proj-a/").And.Contain("alpha-task");
	}

	[Fact]
	public async Task IdentifierFastPath_ResolvesNodeId()
	{
		var added = await Seed(ProjA, "work", new NodePatch { Key = "alpha-task", Title = "Alpha work item", Body = "x" });
		var nodeId = added.Added.Single(n => n.Key == "alpha-task").NodeId;

		var scope = ByWorkspace((Ws: "ws1", Project: Proj(ProjA, "ws1")));
		var hits = await CoreOnly().SearchAsync(scope, nodeId, "https", "box.test");

		hits.Should().ContainSingle();
		hits[0].Key.Should().Be("alpha-task");
		hits[0].NodeId.Should().Be(nodeId);
		hits[0].ExactMatch.Should().BeTrue();
	}

	[Fact]
	public async Task IdentifierFastPath_AmbiguousSlugAcrossBoards_ReturnsAllMatches()
	{
		// exact-identifier-search-surfacing: a slug living on two boards of one project must
		// surface BOTH (each labelled by board). The old exact leg went through the throwing
		// ResolveNodeRefAsync and SWALLOWED the ambiguity (caught → zero exact hits); now every
		// match comes back as an exact-match row — ambiguity is not an error in search.
		await Seed(ProjA, "work", new NodePatch { Key = "dup-slug", Title = "Dup on work", Body = "x" });
		await Seed(ProjA, "notes", new NodePatch { Key = "dup-slug", Title = "Dup on notes", Body = "x" });

		var scope = ByWorkspace((Ws: "ws1", Project: Proj(ProjA, "ws1")));
		var hits = await CoreOnly().SearchAsync(scope, "dup-slug", "https", "box.test");

		hits.Should().HaveCount(2);
		hits.Should().OnlyContain(h => h.ExactMatch && h.Key == "dup-slug" && h.ProjectKey == ProjA);
		hits.Select(h => h.Board).Should().BeEquivalentTo(["notes", "work"]);
	}

	[Fact]
	public async Task MergeOrdering_ExactHitsComeBeforeFullTextHits()
	{
		// proj-a carries the exact slug; proj-b only mentions the same term in a title (a
		// full-text-only match, lexical index — no embedder in this fixture).
		await Seed(ProjA, "work", new NodePatch { Key = "zephyrquartz", Title = "Zephyrquartz", Body = "x" });
		await Seed(ProjB, "work", new NodePatch { Key = "calib-notes", Title = "Notes about zephyrquartz calibration", Body = "x" });

		var scope = ByWorkspace((Ws: "ws1", Project: Proj(ProjA, "ws1")), (Ws: "ws1", Project: Proj(ProjB, "ws1")));
		var hits = await CoreOnly().SearchAsync(scope, "zephyrquartz", "https", "box.test");

		hits.Should().HaveCount(2);
		hits[0].ExactMatch.Should().BeTrue();
		hits[0].ProjectKey.Should().Be(ProjA);
		hits[1].ExactMatch.Should().BeFalse();
		hits[1].ProjectKey.Should().Be(ProjB);
	}

	[Fact]
	public async Task Dedup_SameProjectListedUnderTwoWorkspaceEntries_ReturnsOneHit()
	{
		await Seed(ProjA, "work", new NodePatch { Key = "alpha-task", Title = "Alpha work item", Body = "x" });

		// A contrived (but valid) input: the same project reachable under two workspace
		// buckets. The merge must de-dup by NodeId regardless of how many times the fan-out
		// enumerates the same node.
		var scope = ByWorkspace((Ws: "ws1", Project: Proj(ProjA, "ws1")), (Ws: "ws2", Project: Proj(ProjA, "ws1")));
		var hits = await CoreOnly().SearchAsync(scope, "alpha-task", "https", "box.test");

		hits.Should().ContainSingle();
	}

	[Fact]
	public async Task EmptyQuery_ReturnsEmpty()
	{
		var scope = ByWorkspace((Ws: "ws1", Project: Proj(ProjA, "ws1")));
		(await CoreOnly().SearchAsync(scope, "", "https", "box.test")).Should().BeEmpty();
		(await CoreOnly().SearchAsync(scope, "   ", "https", "box.test")).Should().BeEmpty();
		(await CoreOnly().SearchAsync(scope, null, "https", "box.test")).Should().BeEmpty();
	}

	[Fact]
	public async Task NoAccessibleProjects_ReturnsEmpty()
	{
		var empty = new Dictionary<string, IReadOnlyList<Project>>(StringComparer.Ordinal);
		(await CoreOnly().SearchAsync(empty, "alpha-task", "https", "box.test")).Should().BeEmpty();
	}
}
