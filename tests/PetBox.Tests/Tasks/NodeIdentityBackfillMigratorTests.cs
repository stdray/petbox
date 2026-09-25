using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Services.NodeRef;

namespace PetBox.Tests.Tasks;

// legacy-node-empty-nodeid-404: a handful of existing rows carry an empty NodeId, an empty
// Type, and/or a Status outside their board kind's FSM — tasks_search's raw active-row scan
// still lists them, but tasks_node_get / the UI detail page (both resolve through
// NodeRefResolver, which drops any row with NodeId.Length == 0) 404 forever. This suite
// hand-plants exactly that shape (the same way the real yoba-summarizer row was found) and
// proves NodeIdentityBackfillMigrator repairs it — including the trap that makes an ordinary
// upsert unsuitable for the NodeId-only case (see the migrator's own comment).
public sealed class NodeIdentityBackfillMigratorTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _boards;
	readonly TasksService _tasks;

	public NodeIdentityBackfillMigratorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-nodeid-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TestSchema.Tasks);
		_boards = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(_boards, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	NodeIdentityBackfillMigrator Migrator() => new(_db.Factory(), _factory, _tasks);

	// Hand-plants an active row directly through TemporalStore (bypassing ApplyWorkflow, the
	// same way the real broken row must have been written) with whatever NodeId/Type/Status
	// the caller wants — including the empty/off-FSM combination this migrator exists to fix.
	async Task SeedRawNode(string board, string key, string nodeId, string type, string status)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new TaskNode { Key = key, Version = 0, Board = board, NodeId = nodeId, Status = status, Type = type, Name = key, Body = "idea body" },
		}, partition: n => n.Board == board);
		r.Applied.Should().BeTrue();
	}

	async Task<TaskNode> ReadNode(string board, string key)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var rows = await ctx.GetTable<TaskNode>().Where(n => n.Board == board && n.Key == key && n.ActiveTo == null).ToListAsync();
		return rows.Single();
	}

	[Fact]
	public async Task Migrate_RepairsEmptyNodeIdEmptyTypeAndOffFsmStatus_AndBecomesResolvable()
	{
		await _boards.CreateAsync(Proj, "ideas", description: null, kind: "ideas");
		// Exactly the shape found in prod: empty NodeId, empty type, status "Pending" (not an
		// ideas-FSM status: raw|exploring|review|deferred|accepted|rejected).
		await SeedRawNode("ideas", "summarizer-bot-idea-doc-bbce94", nodeId: "", type: "", status: "Pending");

		// Before the fix: unresolvable by slug, exactly the reported 404.
		var resolver = new NodeRefResolver(_boards);
		var before = await resolver.ResolveSoftNullAsync(Proj, "summarizer-bot-idea-doc-bbce94", "ideas");
		before.Should().BeNull("a row with an empty NodeId must not resolve — see NodeRefResolver.FindActiveBySlug");

		var touched = Migrator().Migrate();
		touched.Should().Be(1);

		var node = await ReadNode("ideas", "summarizer-bot-idea-doc-bbce94");
		node.NodeId.Should().NotBeNullOrEmpty();
		node.Type.Should().Be("idea", "ideas' default type — the kind's first workflow's first type");
		node.Status.Should().Be("raw", "ideas' initial status once a real type resolves a workflow");
		node.Body.Should().Be("idea body", "the repair must never touch content fields it isn't fixing");

		// After the fix: resolves — the same call tasks_node_get and the UI detail page make.
		var after = await resolver.ResolveSoftNullAsync(Proj, "summarizer-bot-idea-doc-bbce94", "ideas");
		after.Should().Be(node.NodeId);

		// Idempotent: nothing left to touch.
		Migrator().Migrate().Should().Be(0);
	}

	[Fact]
	public async Task Migrate_NodeIdOnlyGap_StillPersists_DespiteIdenticalPayload()
	{
		// The trap this migrator exists to avoid: TaskNode.SamePayload does NOT compare NodeId,
		// so an ordinary TemporalStore.UpsertAsync of a row whose Type/Status are ALREADY valid
		// and only NodeId needs fixing would classify as a no-op and silently write nothing.
		// This proves the raw column patch (phase 1) actually lands even in that case.
		await _boards.CreateAsync(Proj, "ideas", description: null, kind: "ideas");
		await SeedRawNode("ideas", "already-valid-idea", nodeId: "", type: "idea", status: "raw");

		var touched = Migrator().Migrate();
		touched.Should().Be(1);

		var node = await ReadNode("ideas", "already-valid-idea");
		node.NodeId.Should().NotBeNullOrEmpty();
		node.Type.Should().Be("idea");
		node.Status.Should().Be("raw");
	}

	[Fact]
	public async Task Migrate_WorkKindEmptyType_LeavesTypeAndStatusAlone_ButStillFixesNodeId()
	{
		// `work` requires an EXPLICIT type (MethodologyRuntime.For returns null for an empty
		// type on `work`) — guessing one here would override a deliberate contract, not repair
		// a gap. NodeId is still fixed: addressability is independent of type/status validity.
		await _boards.CreateAsync(Proj, "work", description: null, kind: "work");
		await SeedRawNode("work", "legacy-work-item", nodeId: "", type: "", status: "Pending");

		Migrator().Migrate();

		var node = await ReadNode("work", "legacy-work-item");
		node.NodeId.Should().NotBeNullOrEmpty("addressability is fixed even when type/status cannot be safely defaulted");
		node.Type.Should().Be("", "work's type is a deliberate choice — never auto-defaulted");
		node.Status.Should().Be("Pending", "left for a human alongside the empty type");
	}

	[Fact]
	public async Task Migrate_AlreadyValidNode_IsNeverTouched()
	{
		await _boards.CreateAsync(Proj, "ideas", description: null, kind: "ideas");
		await SeedRawNode("ideas", "fine-idea", nodeId: Guid.NewGuid().ToString("N"), type: "idea", status: "exploring");

		Migrator().Migrate().Should().Be(0);

		var node = await ReadNode("ideas", "fine-idea");
		node.Version.Should().Be(1, "an already-valid node must not mint a new revision");
	}
}
