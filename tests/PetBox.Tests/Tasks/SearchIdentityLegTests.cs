using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// The IDENTITY leg of tasks search (spec search-identity-leg): exact identifier resolution is a plain
// EQUALITY predicate over the search_meta_alias reference table — NOT a read of the temporal store off
// the side of the ranking. These tests pin the leg's contract end-to-end through SearchNodesAsync:
//   (1) an exact slug resolves rank-1, retriever "exact";
//   (2) a NodeId resolves rank-1, retriever "exact" — slug↔NodeId SYMMETRY (the NodeId is an alias too);
//   (3) the slug's words typed with spaces resolve via the kebab candidate;
//   (4) identity is SUBJECT TO the statusKind facet (tasks-search-drop-terminal-default, supersedes
//       the old "identity ignores terminality"): terminal-OK resolves by default, terminal-CANCEL
//       resolves only when statusKind names it;
//   (5) resolution is project-scoped — a NodeId/slug in one project never leaks into another.
public sealed class SearchIdentityLegTests : IDisposable
{
	const string Proj = "proj";
	const string Proj2 = "proj2";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly CommentService _commentSvc;
	readonly TagStore _tags;

	public SearchIdentityLegTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-searchidentity-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_db.Insert(new Project { Key = Proj2, WorkspaceKey = "ws", Name = "P2", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_store = new TaskBoardStore(_db.Factory(), _factory);
		_relations = new RelationStore(_factory);
		_commentSvc = new CommentService(_factory);
		_tags = new TagStore(_factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// No embedder wired — the semantic leg cannot rescue anything, so what resolves here resolves
	// through the identity leg (or the lexical floor), never through vectors.
	TasksService Service() => new(_store, _relations, _tags, _commentSvc);

	static NodePatch Node(string key, string title, string body) =>
		new() { Key = key, Version = 0, Title = title, Body = body };

	static SearchRequest<TaskNodeFilter, TaskSortBy> Query(string q, string? board = null, string[]? statusKind = null) =>
		new() { Query = q, Filter = new TaskNodeFilter(board, StatusKind: statusKind) };

	static async Task<PlanNodeView> NodeView(TasksService svc, string project, string board, string key) =>
		(await svc.GetAsync(project, board, includeClosed: true)).Nodes.First(n => n.Key == key);

	[Fact]
	public async Task ExactSlug_ResolvesRankOne_ExactRetriever()
	{
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b", [Node("alpha-node", "Заголовок", "Тело без латиницы.")]);

		var res = await svc.SearchNodesAsync(Proj, Query("alpha-node"));

		res.Hits[0].Node.Key.Should().Be("alpha-node");
		res.Hits[0].Retriever.Should().Be("exact");
		res.Hits[0].Score.Should().BeNull(); // an addressed match has no fused score
	}

	[Fact]
	public async Task ExactNodeId_ResolvesRankOne_ExactRetriever_Symmetry()
	{
		// SYMMETRY: the SAME node reached by its NodeId gives the SAME rank-1 identity hit the slug
		// does — the NodeId is an alias in search_meta_alias, so one equality predicate resolves both.
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b", [Node("alpha-node", "Заголовок", "Тело без латиницы.")]);
		var nodeId = (await NodeView(svc, Proj, "b", "alpha-node")).NodeId;

		var bySlug = await svc.SearchNodesAsync(Proj, Query("alpha-node"));
		var byNodeId = await svc.SearchNodesAsync(Proj, Query(nodeId));

		// Both land the same single node, rank 1, retriever "exact", no fused score.
		byNodeId.Hits.Select(h => h.Node.Key).Should().Equal("alpha-node");
		byNodeId.Hits[0].Retriever.Should().Be("exact");
		byNodeId.Hits[0].Score.Should().BeNull();
		byNodeId.Hits[0].Node.NodeId.Should().Be(nodeId);
		// Symmetric with the slug path down to the resolved node.
		byNodeId.Hits[0].Node.NodeId.Should().Be(bySlug.Hits[0].Node.NodeId);
	}

	[Fact]
	public async Task SlugWords_SpacedQuery_ResolveViaKebabCandidate()
	{
		// A caller typing the slug's WORDS with spaces resolves through the kebab candidate (collapse
		// whitespace to a hyphen, casefold). The prose carries no Latin tokens, so no lexical/semantic
		// hit can be the one doing the work — only the identity leg's kebab candidate can.
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b",
			[Node("methodology-redesign", "Редизайн методологии", "Полное описание передела процесса.")]);

		var res = await svc.SearchNodesAsync(Proj, Query("methodology redesign"));

		res.Hits.Select(h => h.Node.Key).Should().Equal("methodology-redesign");
		res.Hits[0].Retriever.Should().Be("exact");
		res.Hits[0].Score.Should().BeNull();
	}

	[Fact]
	public async Task Identity_SubjectToStatusKindFacet_TerminalOkByDefault_TerminalCancelWhenNamed()
	{
		// tasks-search-drop-terminal-default SUPERSEDES the old "identity ignores terminality": the
		// identity leg is now SUBJECT TO the statusKind facet. A terminal-OK (Done/accepted) node still
		// resolves by identifier under a DEFAULT lookup — the facet default keeps terminalok, which is
		// the frame invariant search-before-rework stands on. A terminal-CANCEL node is HIDDEN by a
		// default identifier lookup and resolves only when statusKind names terminalcancel (or
		// includeClosed widens). Identity is truth about findability WITHIN the asked visibility — no
		// terminal override.
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b",
		[
			Node("cancelled-node", "Отменённая", "тело"),
			Node("accepted-node", "Принятая", "тело"),
		]);

		var cancelled = await NodeView(svc, Proj, "b", "cancelled-node");
		await svc.UpsertAsync(Proj, "b", [new NodePatch { Key = "cancelled-node", Version = cancelled.Version, Status = "Cancelled" }]);
		var accepted = await NodeView(svc, Proj, "b", "accepted-node");
		await svc.UpsertAsync(Proj, "b", [new NodePatch { Key = "accepted-node", Version = accepted.Version, Status = "Done" }]);

		var cancelledId = (await NodeView(svc, Proj, "b", "cancelled-node")).NodeId;
		var acceptedId = (await NodeView(svc, Proj, "b", "accepted-node")).NodeId;

		// Accepted (terminal-OK) resolves by slug AND by NodeId under a DEFAULT lookup (frame invariant).
		var aSlug = await svc.SearchNodesAsync(Proj, Query("accepted-node"));
		aSlug.Hits.Select(h => h.Node.Key).Should().Equal("accepted-node");
		aSlug.Hits[0].Retriever.Should().Be("exact");
		var aId = await svc.SearchNodesAsync(Proj, Query(acceptedId));
		aId.Hits.Select(h => h.Node.Key).Should().Equal("accepted-node");
		aId.Hits[0].Node.Status.Should().Be("Done");
		aId.Hits[0].Retriever.Should().Be("exact");

		// Cancelled (terminal-CANCEL) is HIDDEN by a default identifier lookup — the exact leg obeys the
		// facet — by slug AND by NodeId (a content query never surfaced it either).
		(await svc.SearchNodesAsync(Proj, Query("Отменённая"))).Hits.Should().BeEmpty();
		(await svc.SearchNodesAsync(Proj, Query("cancelled-node"))).Hits.Should().BeEmpty();
		(await svc.SearchNodesAsync(Proj, Query(cancelledId))).Hits.Should().BeEmpty();

		// Naming statusKind:[terminalcancel] surfaces it by slug AND NodeId, retriever "exact".
		var cSlug = await svc.SearchNodesAsync(Proj, Query("cancelled-node", statusKind: ["terminalcancel"]));
		cSlug.Hits.Select(h => h.Node.Key).Should().Equal("cancelled-node");
		cSlug.Hits[0].Node.Status.Should().Be("Cancelled");
		cSlug.Hits[0].Retriever.Should().Be("exact");
		var cId = await svc.SearchNodesAsync(Proj, Query(cancelledId, statusKind: ["terminalcancel"]));
		cId.Hits.Select(h => h.Node.Key).Should().Equal("cancelled-node");
		cId.Hits[0].Retriever.Should().Be("exact");
	}

	[Fact]
	public async Task Identity_IsProjectScoped_DoesNotLeakAcrossProjects()
	{
		// The identity predicate is scoped to the project: a slug/NodeId that exists in one project must
		// never resolve in another. Both projects hold a node with the SAME slug — a leak would surface
		// the wrong project's node (or two).
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.CreateBoardAsync(Proj2, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b", [Node("shared-slug", "В проекте один", "тело")]);
		await svc.UpsertAsync(Proj2, "b", [Node("shared-slug", "В проекте два", "тело")]);

		var p2NodeId = (await NodeView(svc, Proj2, "b", "shared-slug")).NodeId;

		// Slug query in Proj sees ONLY Proj's node (single hit, its own title).
		var slugInProj = await svc.SearchNodesAsync(Proj, Query("shared-slug"));
		slugInProj.Hits.Should().ContainSingle();
		slugInProj.Hits[0].Node.Title.Should().Be("В проекте один");

		// Proj2's NodeId does NOT resolve in Proj — a different scope's alias is invisible here.
		(await svc.SearchNodesAsync(Proj, Query(p2NodeId))).Hits.Should().BeEmpty();

		// ...but it DOES resolve in its own project (proves the miss above is isolation, not a dead index).
		var inProj2 = await svc.SearchNodesAsync(Proj2, Query(p2NodeId));
		inProj2.Hits.Select(h => h.Node.Key).Should().Equal("shared-slug");
		inProj2.Hits[0].Node.Title.Should().Be("В проекте два");
	}
}
