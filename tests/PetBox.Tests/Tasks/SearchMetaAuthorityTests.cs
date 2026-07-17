using LinqToDB;
using LinqToDB.Mapping;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;

namespace PetBox.Tests.Tasks;

// The Class-A META reference layer (spec search-index-authority): search_meta + search_meta_alias are
// the single authority of a node's index membership and its query facets, written in the SAME entity
// transaction as the search_fts text row. These tests pin the WRITE side only (the reads — facet
// pushdown, identity resolution — are separate legs): the upsert seam populates the facet row and the
// alias set (slug AND NodeId — the identifier the lexical index does not carry); StatusKind comes from
// the MethodologyRuntime authority and follows a status change in place; and a file predating the
// table self-heals via the version-gated backfill on the next search.
public sealed class SearchMetaAuthorityTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _store;
	readonly RelationStore _relations;
	readonly CommentService _commentSvc;
	readonly TagStore _tags;

	public SearchMetaAuthorityTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-searchmeta-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
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

	TasksService Service() => new(_store, _relations, _tags, _commentSvc);

	static NodePatch Node(string key, string title, string body) =>
		new() { Key = key, Version = 0, Title = title, Body = body };

	static SearchRequest<TaskNodeFilter, TaskSortBy> Query(string q, string? board = null) =>
		new() { Query = q, Filter = new TaskNodeFilter(board) };

	// ---- raw reference-layer reads ----

	List<MetaRow> Meta(string board)
	{
		using var conn = _factory.NewEnsuredConnection(Proj);
		return conn.GetTable<MetaRow>().Where(r => r.Scope == Proj && r.Type == board).ToList();
	}

	List<string> Aliases(string board, string id)
	{
		using var conn = _factory.NewEnsuredConnection(Proj);
		return conn.GetTable<AliasRow>().Where(r => r.Scope == Proj && r.Type == board && r.Id == id)
			.Select(r => r.Alias).ToList();
	}

	static async Task<string> NodeIdOf(TasksService svc, string board, string key) =>
		(await svc.GetNodeOnBoardAsync(Proj, board, key)).Node.NodeId;

	[Fact]
	public async Task Upsert_writes_the_facet_row_and_both_aliases()
	{
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b", [Node("alpha", "alpha note", "body")]);

		var nodeId = await NodeIdOf(svc, "b", "alpha");

		var rows = Meta("b");
		rows.Should().ContainSingle();
		var row = rows[0];
		row.Id.Should().Be("alpha");
		row.StatusKind.Should().Be("open"); // a fresh simple node is in an open status
		row.Created.Should().NotBeNullOrWhiteSpace();
		row.Updated.Should().NotBeNullOrWhiteSpace();

		// The alias set carries the slug AND the NodeId — the NodeId is the identifier the lexical
		// index does NOT carry, the hole this reference layer closes.
		Aliases("b", "alpha").Should().BeEquivalentTo(["alpha", nodeId]);
	}

	[Fact]
	public async Task StatusKind_is_taken_from_the_authority_and_follows_a_status_change()
	{
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b", [Node("alpha", "alpha", "body")]);
		Meta("b")[0].StatusKind.Should().Be("open");

		// Simple boards have free all-pairs transitions, so Todo→Done is valid. The facet must be
		// re-projected from the authority (StatusKindOf: Done = TerminalOk) in the SAME transaction —
		// and the row survives, because membership no longer forks on terminality.
		var version = (await svc.GetNodeOnBoardAsync(Proj, "b", "alpha")).Node.Version;
		await svc.UpsertAsync(Proj, "b", [new NodePatch { Key = "alpha", Version = version, Status = "Done" }]);

		var rows = Meta("b");
		rows.Should().ContainSingle();
		rows[0].StatusKind.Should().Be("terminalok");

		// A terminal-cancel status classifies the other way — proving the value is the authority's, not
		// a hardcoded "open".
		var v2 = (await svc.GetNodeOnBoardAsync(Proj, "b", "alpha")).Node.Version;
		await svc.UpsertAsync(Proj, "b", [new NodePatch { Key = "alpha", Version = v2, Status = "Cancelled" }]);
		Meta("b")[0].StatusKind.Should().Be("terminalcancel");
	}

	[Fact]
	public async Task Deleting_a_node_drops_its_facet_row_and_aliases()
	{
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b", [Node("alpha", "alpha", "body")]);
		Meta("b").Should().ContainSingle();

		await svc.UpsertAsync(Proj, "b", [new NodePatch { Key = "alpha", Version = 0, Deleted = true }]);

		Meta("b").Should().BeEmpty();
		Aliases("b", "alpha").Should().BeEmpty();
	}

	[Fact]
	public async Task Backfill_rebuilds_the_reference_layer_for_preexisting_nodes()
	{
		var svc = Service();
		await svc.CreateBoardAsync(Proj, "b", "simple", null, null);
		await svc.UpsertAsync(Proj, "b", [Node("alpha", "alpha", "body"), Node("beta", "beta", "body")]);
		var alphaId = await NodeIdOf(svc, "b", "alpha");

		// Simulate a file that predates search_meta: wipe its rows and rewind the projection marker, so
		// the ONLY way the rows can come back is EnsureMetaBackfillAsync on the next search.
		using (var conn = _factory.NewEnsuredConnection(Proj))
		{
			conn.GetTable<MetaRow>().Delete();
			conn.GetTable<AliasRow>().Delete();
			conn.GetTable<CursorRow>().Where(r => r.IndexName == TasksCursors.Meta).Delete();
		}
		Meta("b").Should().BeEmpty();

		await svc.SearchNodesAsync(Proj, Query("alpha", "b"));

		Meta("b").Select(r => r.Id).Should().BeEquivalentTo(["alpha", "beta"]);
		Aliases("b", "alpha").Should().BeEquivalentTo(["alpha", alphaId]);
	}

	[Table("search_meta")]
	sealed class MetaRow
	{
		[Column] public string Scope { get; set; } = string.Empty;
		[Column] public string Type { get; set; } = string.Empty;
		[Column] public string Id { get; set; } = string.Empty;
		[Column] public string StatusKind { get; set; } = string.Empty;
		[Column] public string Created { get; set; } = string.Empty;
		[Column] public string Updated { get; set; } = string.Empty;
	}

	[Table("search_meta_alias")]
	sealed class AliasRow
	{
		[Column] public string Scope { get; set; } = string.Empty;
		[Column] public string Type { get; set; } = string.Empty;
		[Column] public string Id { get; set; } = string.Empty;
		[Column] public string Alias { get; set; } = string.Empty;
	}

	[Table("search_cursor")]
	sealed class CursorRow
	{
		[Column] public string IndexName { get; set; } = string.Empty;
		[Column] public long Version { get; set; }
	}
}
