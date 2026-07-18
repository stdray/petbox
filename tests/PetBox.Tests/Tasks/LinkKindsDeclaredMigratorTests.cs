using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// methodology-link-kinds-declared: the quartet's process link kinds (idea_spec/task_spec/issue_task)
// moved out of MethodologyRuntime.ProcessRelationKinds into declared linkKinds with direction, and
// delivery names its link kind as DATA (delivery.link). A document materialized BEFORE this change
// references the trio (constraints/effects) and rolls up delivery by an implicit literal but
// declares neither. This suite builds that pre-change shape by hand and proves
// LinkKindsDeclaredMigrator backfills it: the trio is declared with its canonical direction, and
// delivery.link is set to the old literal — idempotently, and only for slugs the document already
// references, leaving a project's own kinds untouched.
public sealed class LinkKindsDeclaredMigratorTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _boards;

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	// A `work` kind exactly as a stored quartet document read BEFORE this change: task_spec
	// constraints + issue_task/blocks effects REFERENCE the trio, but no linkKind declares it.
	static readonly MethodologyKindDef PreWorkKind = new("work", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["feature", "bug", "chore"],
			[
				new("Pending", "Pending", StatusKind.Open),
				new("InProgress", "In progress", StatusKind.Open),
				new("Done", "Done", StatusKind.TerminalOk),
				new("Blocked", "Blocked", StatusKind.Open),
				new("Cancelled", "Cancelled", StatusKind.TerminalCancel),
			],
			[
				new("Pending", "InProgress"),
				new("InProgress", "Done", RequiresApproval: true),
			]),
	])
	{
		LinkConstraints =
		[
			new MethodologyLinkConstraintDef("feature", "task_spec") { TargetKind = "spec" },
			new MethodologyLinkConstraintDef("bug", "task_spec") { TargetKind = "spec" },
		],
		Effects =
		[
			new MethodologyTransitionEffectDef(On: "Done", Link: "issue_task", Direction: "incoming", Set: "done"),
			new MethodologyTransitionEffectDef(On: "Done", Link: "blocks", Direction: "outgoing", Set: "InProgress", OnlyFrom: "Blocked"),
		],
	};

	// A `spec` kind: idea_spec constraint REFERENCES the trio, and delivery has an EMPTY link (the
	// pre-field state — the roll-up went by the task_spec literal, no `link` stored).
	static readonly MethodologyKindDef PreSpecKind = new("spec", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["spec"],
			[
				new("defined", "Defined", StatusKind.Open),
				new("deprecated", "Deprecated", StatusKind.TerminalCancel),
			],
			[new("defined", "deprecated")]),
	])
	{
		LinkConstraints = [new MethodologyLinkConstraintDef("spec", "idea_spec") { TargetKind = "ideas", TargetStatuses = ["accepted"] }],
		Delivery = new MethodologyDeliveryDef(["feature"], ["bug"], ""),
	};

	static readonly MethodologyKindDef IdeasKind = new("ideas", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["idea"], [new("accepted", "Accepted", StatusKind.TerminalOk)], []),
	]);

	static readonly MethodologyKindDef IntakeKind = new("intake", QuickAddAllowed: true,
	[
		new MethodologyWorkflowDef(["issue"], [new("reported", "Reported", StatusKind.Open)], []),
	]);

	// The whole pre-change quartet document: references the trio everywhere, declares none of it,
	// delivery.link empty.
	static readonly MethodologyDefinition PreQuartet =
		new("legacy-quartet", [IntakeKind, IdeasKind, PreSpecKind, PreWorkKind]);

	public LinkKindsDeclaredMigratorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-lkdm-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
		_boards = new TaskBoardStore(_db.Factory(), _factory);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	LinkKindsDeclaredMigrator Migrator() => new(_db.Factory(), _factory);

	Task SeedProjectBoard() =>
		_boards.CreateAsync(Proj, "work", description: null, kind: "work", methodologyInstance: "quartet");

	async Task SeedInstance(string key, MethodologyDefinition def)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		(await TemporalStore.UpsertAsync(ctx, new[]
		{
			new MethodologyInstanceRow { Key = key, Version = 0, Json = JsonSerializer.Serialize(def, DefinitionJson), ClosedAt = null },
		})).Applied.Should().BeTrue();
	}

	async Task<MethodologyDefinition> ReadInstance(string key)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var row = (await ctx.GetTable<MethodologyInstanceRow>().Where(r => r.Key == key && r.ActiveTo == null).ToListAsync()).Single();
		return JsonSerializer.Deserialize<MethodologyDefinition>(row.Json, DefinitionJson)!;
	}

	static MethodologyLinkKindDef Quartet(string slug) =>
		MethodologyPresets.QuartetLinkKinds.Single(k => k.Slug == slug);

	[Fact]
	public async Task PreChangeDocument_DeclaresTheTrioWithDirection_AndBackfillsDeliveryLink()
	{
		await SeedProjectBoard();
		await SeedInstance("quartet", PreQuartet);

		Migrator().Migrate().Should().Be(1);

		var def = await ReadInstance("quartet");

		// The three referenced process kinds are now declared, each with its canonical direction.
		foreach (var slug in new[] { "idea_spec", "task_spec", "issue_task" })
		{
			var declared = def.LinkKinds.SingleOrDefault(lk => lk.Slug == slug);
			declared.Should().NotBeNull($"'{slug}' is referenced by a constraint/effect, so it must be declared");
			declared!.Category.Should().Be(LinkCategory.Process);
			declared.Direction.Should().BeEquivalentTo(Quartet(slug).Direction);
		}

		// delivery.link backfilled to the old literal the roll-up went by.
		def.Kinds.Single(k => k.Kind == "spec").Delivery!.Link.Should().Be("task_spec");

		// The document still resolves as a valid runtime (the trio is a valid vocabulary).
		var runtime = new MethodologyRuntime(def);
		runtime.LinkKind("task_spec")!.Direction!.ToKind.Should().Be("spec");
	}

	[Fact]
	public async Task SecondRun_IsANoOp()
	{
		await SeedProjectBoard();
		await SeedInstance("quartet", PreQuartet);
		Migrator().Migrate().Should().Be(1);

		Migrator().Migrate().Should().Be(0, "an already-declared document must not be rewritten again");
	}

	[Fact]
	public async Task CustomDeclaredTrio_IsNotOverwritten()
	{
		// A project that already DECLARES task_spec with its OWN direction is a deliberate
		// customization — the migrator must not touch it (it only injects for a slug the document
		// does not already declare). It still declares the OTHER referenced trio slugs.
		var customTaskSpec = new MethodologyLinkKindDef("task_spec", "custom", LinkCategory.Process,
			new MethodologyLinkDirectionDef("work", "spec", "custom-label"));
		var def = PreQuartet with { LinkKinds = [customTaskSpec] };

		await SeedProjectBoard();
		await SeedInstance("quartet", def);

		Migrator().Migrate().Should().Be(1);

		var read = await ReadInstance("quartet");
		read.LinkKinds.Single(lk => lk.Slug == "task_spec").Direction!.Label
			.Should().Be("custom-label", "a project's own declaration of the slug is preserved");
		read.LinkKinds.Select(lk => lk.Slug).Should().Contain(["idea_spec", "issue_task"],
			"the other referenced trio slugs are still declared");
	}

	[Fact]
	public async Task NonQuartetReuseOfTrioSlug_WithoutCanonicalEndKinds_LeftUntouched()
	{
		// A project's OWN process reuses the slug `task_spec` on kinds that are NOT the quartet's
		// work/spec — it declares neither. Injecting the canonical work→spec direction would silently
		// start rejecting that edge's relations_create and, on the next rules_upsert, fail validation
		// (the injected ends aren't its declared kinds), locking the owner out of their own
		// methodology. The migrator must recognize this is not the quartet shape and leave it as-is.
		var alpha = new MethodologyKindDef("alpha", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["a"], [new("open", "Open", StatusKind.Open)], []),
		])
		{
			LinkConstraints = [new MethodologyLinkConstraintDef("a", "task_spec") { TargetKind = "beta" }],
		};
		var beta = new MethodologyKindDef("beta", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["b"], [new("open", "Open", StatusKind.Open)], []),
		]);
		var own = new MethodologyDefinition("own", [alpha, beta]);

		await SeedProjectBoard();
		await SeedInstance("own", own);

		Migrator().Migrate().Should().Be(0,
			"the trio slug is reused without the canonical work/spec end-kinds — not our quartet shape");
		JsonSerializer.Serialize(await ReadInstance("own"), DefinitionJson)
			.Should().Be(JsonSerializer.Serialize(own, DefinitionJson), "left byte-for-byte untouched");
	}

	[Fact]
	public async Task DocumentThatReferencesNoTrio_AndHasNoDelivery_LeftUntouched()
	{
		// A project's own methodology that mentions none of the trio and rolls up nothing: nothing
		// to declare, nothing to backfill.
		var ownKind = new MethodologyKindDef("task", QuickAddAllowed: true,
		[
			new MethodologyWorkflowDef(["task"], [new("todo", "Todo", StatusKind.Open)], []),
		]);
		var own = new MethodologyDefinition("own", [ownKind]);

		await SeedProjectBoard();
		await SeedInstance("own", own);

		Migrator().Migrate().Should().Be(0);
		JsonSerializer.Serialize(await ReadInstance("own"), DefinitionJson)
			.Should().Be(JsonSerializer.Serialize(own, DefinitionJson));
	}
}
