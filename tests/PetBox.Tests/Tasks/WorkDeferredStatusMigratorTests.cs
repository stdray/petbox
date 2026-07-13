using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Services.Methodology;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// work-preset-drop-deferred: MethodologyPresets.WorkKind no longer declares the `Deferred`
// status. That alone does not repair a definition/instance already MATERIALIZED (verbatim-
// copied, RenderBuiltinTemplate) into a project's stored document before the change —
// exactly the board-view-defaults-not-applied-existing-instances class of miss, except here
// the affected fields (statuses/transitions) are PROCESS fields MethodologyRuntime reads
// WHOLE-OBJECT from the stored document by design, so a per-field merge is the wrong fix;
// a one-time migration of the stored document is the right one. This suite builds the OLD
// (pre-fix) materialized shape by hand — the same trap MethodologyRuntimeViewDefaultsTests
// exists for on the display-field side — and proves WorkDeferredStatusMigrator repairs it.
public sealed class WorkDeferredStatusMigratorTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _boards;
	readonly TasksService _tasks;

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	// The `work` kind exactly as MethodologyPresets.WorkKind used to read BEFORE
	// work-preset-drop-deferred (7 statuses / 11 transitions, Deferred included) — what a
	// project's stored document looked like when it was materialized from the OLD preset.
	static readonly MethodologyKindDef OldWorkKind = new("work", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["feature", "bug", "chore"],
			[
				new("Pending", "Pending", StatusKind.Open),
				new("InProgress", "In progress", StatusKind.Open),
				new("Review", "Review", StatusKind.Open),
				new("Done", "Done", StatusKind.TerminalOk),
				new("Blocked", "Blocked", StatusKind.Open),
				new("Deferred", "Deferred", StatusKind.Open),
				new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
			],
			[
				new("Pending", "InProgress"),
				new("InProgress", "Review"),
				new("Review", "InProgress"),
				new("Review", "Done", RequiresApproval: true),
				new("InProgress", "Blocked"),
				new("Blocked", "InProgress"),
				new("Pending", "Deferred"),
				new("Deferred", "Pending"),
				new("Pending", "Cancelled"),
				new("InProgress", "Cancelled"),
				new("Review", "Cancelled"),
			]),
	])
	{
		LinkConstraints =
		[
			new MethodologyLinkConstraintDef("feature", "task_spec") { TargetKind = "spec" },
			new MethodologyLinkConstraintDef("bug", "task_spec") { TargetKind = "spec" },
		],
		DefaultView = BoardViewModeNames.Kanban,
	};

	static readonly MethodologyKindDef OldSpecKind = new("spec", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["spec"],
			[
				new("defined", "Defined", StatusKind.Open),
				new("deprecated", "Deprecated", StatusKind.TerminalCancel),
			],
			[
				new("defined", "deprecated"),
			]),
	]);

	static readonly MethodologyDefinition OldQuartet = new("legacy-quartet", [OldWorkKind, OldSpecKind]);

	public WorkDeferredStatusMigratorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-wdsm-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_boards = new TaskBoardStore(_db.Factory(), _factory);
		_tasks = new TasksService(_boards, new RelationStore(_factory), new TagStore(_factory), new CommentService(_factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	WorkDeferredStatusMigrator Migrator() => new(_db.Factory(), _factory);

	// Migrate() discovers the projects to scan from Core's TaskBoards catalog (same as
	// MethodologyInstanceBackfill) — a project with no board at all never gets visited. Every
	// test that expects the migrator to actually reach `Proj` seeds one board first.
	// `methodologyInstance` binds the board into a named instance's scope (needed for the
	// node-move tests — the migrator scopes an instance's node search to its member boards).
	async Task SeedProjectBoard(string? methodologyInstance = null) =>
		await _boards.CreateAsync(Proj, "work", description: null, kind: "work", methodologyInstance: methodologyInstance);

	// Hand-writes an active PlanNode directly (bypassing the FSM-guarded upsert path — the
	// point is to plant a node in a status the CURRENT definition may not even have an edge
	// into, exactly the "found a straggler" scenario the migrator exists to fix).
	async Task<string> SeedNode(string board, string key, string status, string type = "feature")
	{
		var nodeId = Guid.NewGuid().ToString("N");
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new PlanNode { Key = key, Version = 0, Board = board, NodeId = nodeId, Status = status, Type = type, Name = key, Body = "" },
		}, partition: n => n.Board == board);
		r.Applied.Should().BeTrue();
		return nodeId;
	}

	async Task<PlanNode> ReadNode(string board, string key)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var rows = await ctx.GetTable<PlanNode>().Where(n => n.Board == board && n.Key == key && n.ActiveTo == null).ToListAsync();
		return rows.Single();
	}

	async Task<List<CommentRow>> ReadComments(string nodeId)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		return await ctx.GetTable<CommentRow>().Where(c => c.NodeId == nodeId && c.ActiveTo == null).ToListAsync();
	}

	async Task SeedInstance(string key, MethodologyDefinition def)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new MethodologyInstanceRow { Key = key, Version = 0, Json = JsonSerializer.Serialize(def, DefinitionJson), ClosedAt = null },
		});
		r.Applied.Should().BeTrue();
	}

	async Task SeedProjectDef(MethodologyDefinition def)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var r = await TemporalStore.UpsertAsync(ctx, new[]
		{
			new MethodologyDefRow { Key = MethodologyDefRow.SingletonKey, Version = 0, Json = JsonSerializer.Serialize(def, DefinitionJson) },
		});
		r.Applied.Should().BeTrue();
	}

	async Task<MethodologyDefinition> ReadInstance(string key)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var rows = await ctx.GetTable<MethodologyInstanceRow>().Where(r => r.Key == key && r.ActiveTo == null).ToListAsync();
		var row = rows.Single();
		return JsonSerializer.Deserialize<MethodologyDefinition>(row.Json, DefinitionJson)!;
	}

	async Task<MethodologyDefinition> ReadProjectDef()
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var rows = await ctx.GetTable<MethodologyDefRow>().Where(r => r.Key == MethodologyDefRow.SingletonKey && r.ActiveTo == null).ToListAsync();
		var row = rows.Single();
		return JsonSerializer.Deserialize<MethodologyDefinition>(row.Json, DefinitionJson)!;
	}

	static void AssertDeferredGone(MethodologyDefinition def)
	{
		var work = def.Kinds.Single(k => k.Kind == "work");
		var block = work.Workflows.Single();
		block.Statuses.Select(s => s.Slug).Should().NotContain("Deferred");
		block.Statuses.Select(s => s.Slug).Should().BeEquivalentTo(
			["Pending", "InProgress", "Review", "Done", "Blocked", "Cancelled"],
			"the migration removes ONLY the Deferred status — everything else is untouched");
		block.Transitions.Should().NotContain(t => t.From == "Deferred" || t.To == "Deferred",
			"a dropped status must not leave dangling FSM edges");
		block.Transitions.Should().HaveCount(9, "11 old transitions minus the 2 that named Deferred");

		// The document must still resolve as a valid runtime — no crash, real FSM.
		var runtime = new MethodologyRuntime(def);
		var wf = runtime.For("work", "feature");
		wf.Should().NotBeNull();
		wf!.Statuses.Select(s => s.Slug).Should().NotContain("Deferred");
	}

	[Fact]
	public async Task OldMaterializedInstance_DeferredStatusAndTransitions_Stripped()
	{
		await SeedProjectBoard();
		await SeedInstance("quartet", OldQuartet);

		var rewritten = Migrator().Migrate();
		rewritten.Should().Be(1);

		AssertDeferredGone(await ReadInstance("quartet"));
	}

	[Fact]
	public async Task OldProjectSingletonDef_DeferredStatusAndTransitions_Stripped()
	{
		await SeedProjectBoard();
		await SeedProjectDef(OldQuartet);

		var rewritten = Migrator().Migrate();
		rewritten.Should().Be(1);

		AssertDeferredGone(await ReadProjectDef());
	}

	[Fact]
	public async Task BothProjectDefAndInstance_BothStripped_OneMigrateCall()
	{
		await SeedProjectBoard();
		await SeedProjectDef(OldQuartet);
		await SeedInstance("quartet", OldQuartet);

		Migrator().Migrate().Should().Be(2, "both the singleton def and the instance rules carry the stale work kind");

		AssertDeferredGone(await ReadProjectDef());
		AssertDeferredGone(await ReadInstance("quartet"));
	}

	[Fact]
	public async Task SecondRun_IsANoOp()
	{
		await SeedProjectBoard();
		await SeedInstance("quartet", OldQuartet);
		Migrator().Migrate().Should().Be(1);

		Migrator().Migrate().Should().Be(0, "already-migrated documents must not be rewritten again");
	}

	[Fact]
	public async Task DocumentWithoutWorkKind_LeftUntouched()
	{
		await SeedProjectBoard();
		var specOnly = new MethodologyDefinition("spec-only", [OldSpecKind]);
		await SeedInstance("specs", specOnly);

		Migrator().Migrate().Should().Be(0);

		var read = await ReadInstance("specs");
		JsonSerializer.Serialize(read, DefinitionJson).Should().Be(JsonSerializer.Serialize(specOnly, DefinitionJson));
	}

	[Fact]
	public async Task DocumentAlreadyOnTheNewPreset_LeftUntouched()
	{
		await SeedProjectBoard();
		// A definition materialized from the CURRENT (already-fixed) builtin preset carries no
		// Deferred to begin with — the migrator must not touch it.
		var current = MethodologyPresets.RenderBuiltinTemplate("quartet");
		await SeedInstance("quartet", current);

		Migrator().Migrate().Should().Be(0);
	}

	// The maintainer's call (work-preset-drop-deferred): a straggler node in `Deferred` on an
	// untouched-copy-of-our-preset document is moved to `Cancelled` — with a recorded reason —
	// BEFORE the status is stripped from the definition, so there is never a node pointing at a
	// status the definition no longer has.
	[Fact]
	public async Task OldMaterializedInstance_WithDeferredNodes_MovesThemToCancelledWithReason_ThenStripsStatus()
	{
		await SeedProjectBoard(methodologyInstance: "quartet");
		await SeedInstance("quartet", OldQuartet);
		var nodeId1 = await SeedNode("work", "f1", "Deferred");
		var nodeId2 = await SeedNode("work", "f2", "Deferred", type: "bug");
		// A node in some OTHER status must not be touched at all.
		await SeedNode("work", "f3", "Pending");

		Migrator().Migrate().Should().Be(1, "one document (the instance) was rewritten");

		(await ReadNode("work", "f1")).Status.Should().Be("Cancelled");
		(await ReadNode("work", "f2")).Status.Should().Be("Cancelled");
		(await ReadNode("work", "f3")).Status.Should().Be("Pending", "only Deferred nodes are touched");

		var comments1 = await ReadComments(nodeId1);
		comments1.Should().ContainSingle(c => c.Body.Contains("Deferred", StringComparison.Ordinal) && c.Author == "system");
		var comments2 = await ReadComments(nodeId2);
		comments2.Should().ContainSingle(c => c.Body.Contains("Deferred", StringComparison.Ordinal));

		AssertDeferredGone(await ReadInstance("quartet"));
	}

	// The core of this review round: a `work` kind that CARRIES `Deferred` but does NOT match
	// our old preset exactly (customized, or a project's own methodology) must be left
	// completely alone — the document AND every node on it, Deferred or not.
	[Fact]
	public async Task CustomizedWorkKind_WithDeferredNodes_DocumentAndNodesUntouched()
	{
		// Same statuses as the old preset, but ONE extra status added to the block — no longer
		// a byte-for-byte match, so this reads as a project's OWN customization, not our copy.
		var customizedWorkKind = OldWorkKind with
		{
			Workflows =
			[
				OldWorkKind.Workflows[0] with
				{
					Statuses = [.. OldWorkKind.Workflows[0].Statuses, new WorkflowStatus("Icebox", "Icebox", StatusKind.Open)],
				},
			],
		};
		var customDef = new MethodologyDefinition("custom-flow", [customizedWorkKind]);

		await SeedProjectBoard(methodologyInstance: "custom");
		await SeedInstance("custom", customDef);
		var nodeId = await SeedNode("work", "f1", "Deferred");

		Migrator().Migrate().Should().Be(0, "a customized work kind is not our verbatim copy — nothing is rewritten");

		(await ReadNode("work", "f1")).Status.Should().Be("Deferred", "an untouched document keeps its nodes untouched too");
		(await ReadComments(nodeId)).Should().BeEmpty("no reason comment is added when nothing is migrated");

		var read = await ReadInstance("custom");
		read.Kinds.Single(k => k.Kind == "work").Workflows.Single().Statuses
			.Should().Contain(s => s.Slug == "Deferred", "the customized document is untouched, Deferred and all");
	}

	// The other half of the acceptance bar: a BRAND NEW instance, created through the real
	// service path (not hand-seeded), must never have carried Deferred in the first place —
	// proving the preset edit reaches normal creation without any migration involved.
	[Fact]
	public async Task NewInstance_CreatedFromBuiltinQuartet_HasNoDeferredStatus()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "fresh", MethodologyInstanceService.SourceBuiltin, "quartet");

		var rules = await _tasks.GetMethodologyInstanceRulesAsync(Proj, "fresh");
		rules.Should().NotBeNull();
		var work = rules!.Definition.Kinds.Single(k => k.Kind == "work");
		work.Workflows.Single().Statuses.Select(s => s.Slug).Should().NotContain("Deferred");
	}
}
