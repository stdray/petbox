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
	async Task SeedProjectBoard() => await _boards.CreateAsync(Proj, "work", description: null, kind: "work");

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
