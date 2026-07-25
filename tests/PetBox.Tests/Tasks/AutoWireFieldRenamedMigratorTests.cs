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

// delivery-autowire-still-hardcoded-spec: the auto-wire target field was renamed
// AutoWireSpecFrom -> AutoWireFrom (JSON key autoWireSpecFrom -> autoWireFrom). A methodology
// document materialized BEFORE the rename (the live quartet v16 included) still carries the OLD
// key, which the renamed deserializer no longer binds — it would read null and auto-wire / set-wire
// validation would silently die. This suite builds that pre-rename shape by hand (serialize the new
// shape, then rewrite the key back to its old spelling — exactly what a stored v16 row looks like)
// and proves AutoWireFieldRenamedMigrator renames it: the value binds again through the typed model,
// idempotently, across definition/instance/template rows, and a document that never carried the key
// is left byte-for-byte untouched.
public sealed class AutoWireFieldRenamedMigratorTests : IDisposable
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

	// A `work` kind carrying AutoWireFrom="spec" (the quartet auto-wire), plus a plain `spec` kind.
	static readonly MethodologyKindDef WorkKind = new("work", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["feature", "bug", "chore"],
			[
				new("Pending", "Pending", StatusKind.Open),
				new("Done", "Done", StatusKind.TerminalOk),
			],
			[new("Pending", "Done")]),
	])
	{
		AutoWireFrom = "spec",
	};

	static readonly MethodologyKindDef SpecKind = new("spec", QuickAddAllowed: false,
	[
		new MethodologyWorkflowDef(["spec"],
			[new("defined", "Defined", StatusKind.Open)],
			[]),
	]);

	static readonly MethodologyDefinition Quartet = new("quartet", [SpecKind, WorkKind]);

	// The pre-rename stored shape: serialize the NEW model, then spell the key the OLD way — exactly
	// what a v16 row carries (the value "spec" is unchanged; only the field name moved).
	static string PreRenameJson(MethodologyDefinition def) =>
		JsonSerializer.Serialize(def, DefinitionJson).Replace("autoWireFrom", "autoWireSpecFrom");

	public AutoWireFieldRenamedMigratorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-awfr-" + Guid.NewGuid().ToString("N"));
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

	AutoWireFieldRenamedMigrator Migrator() => new(_db.Factory(), _factory);

	Task SeedProjectBoard() =>
		_boards.CreateAsync(Proj, "work", description: null, kind: "work", methodologyInstance: "quartet");

	async Task SeedInstanceRaw(string key, string json)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		(await TemporalStore.UpsertAsync(ctx, new[]
		{
			new MethodologyInstanceRow { Key = key, Version = 0, Json = json, ClosedAt = null },
		})).Applied.Should().BeTrue();
	}

	async Task SeedDefinitionRaw(string json)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		(await TemporalStore.UpsertAsync(ctx, new[]
		{
			new MethodologyDefRow { Key = MethodologyDefRow.SingletonKey, Version = 0, Json = json },
		})).Applied.Should().BeTrue();
	}

	async Task SeedTemplateRaw(string key, string json)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		(await TemporalStore.UpsertAsync(ctx, new[]
		{
			new MethodologyTemplateRow { Key = key, Version = 0, Json = json },
		})).Applied.Should().BeTrue();
	}

	async Task<string> ReadInstanceJson(string key)
	{
		using var ctx = _factory.NewEnsuredConnection(Proj);
		var row = (await ctx.GetTable<MethodologyInstanceRow>().Where(r => r.Key == key && r.ActiveTo == null).ToListAsync()).Single();
		return row.Json;
	}

	[Fact]
	public async Task PreRenameInstance_RenamesKey_AndValueBindsThroughTypedModel()
	{
		await SeedProjectBoard();
		var before = PreRenameJson(Quartet);
		before.Should().Contain("autoWireSpecFrom").And.NotContain("\"autoWireFrom\"");
		await SeedInstanceRaw("quartet", before);

		Migrator().Migrate().Should().Be(1);

		var json = await ReadInstanceJson("quartet");
		json.Should().NotContain("autoWireSpecFrom", "the old key must be gone");

		// The renamed key now binds through the typed model — auto-wire reads "spec" again.
		var def = JsonSerializer.Deserialize<MethodologyDefinition>(json, DefinitionJson)!;
		def.Kinds.Single(k => k.Kind == "work").AutoWireFrom.Should().Be("spec");
		new MethodologyRuntime(def).AutoWireFrom("work").Should().Be("spec");
	}

	[Fact]
	public async Task SecondRun_IsANoOp()
	{
		await SeedProjectBoard();
		await SeedInstanceRaw("quartet", PreRenameJson(Quartet));
		Migrator().Migrate().Should().Be(1);

		Migrator().Migrate().Should().Be(0, "an already-renamed document must not be rewritten again");
	}

	[Fact]
	public async Task DefinitionAndTemplateRows_AreAlsoMigrated()
	{
		await SeedProjectBoard();
		await SeedDefinitionRaw(PreRenameJson(Quartet));
		await SeedTemplateRaw("tmpl", PreRenameJson(Quartet));

		// definition singleton + template = two documents rewritten.
		Migrator().Migrate().Should().Be(2);

		using var ctx = _factory.NewEnsuredConnection(Proj);
		var def = (await ctx.GetTable<MethodologyDefRow>().Where(r => r.Key == MethodologyDefRow.SingletonKey && r.ActiveTo == null).ToListAsync()).Single();
		def.Json.Should().Contain("autoWireFrom").And.NotContain("autoWireSpecFrom");
		var tmpl = (await ctx.GetTable<MethodologyTemplateRow>().Where(r => r.Key == "tmpl" && r.ActiveTo == null).ToListAsync()).Single();
		tmpl.Json.Should().Contain("autoWireFrom").And.NotContain("autoWireSpecFrom");
	}

	[Fact]
	public async Task DocumentWithoutTheKey_LeftByteForByteUntouched()
	{
		// A project's own methodology whose kinds declare no auto-wire at all: nothing to rename.
		var own = new MethodologyDefinition("own",
		[
			new("task", QuickAddAllowed: true,
			[
				new MethodologyWorkflowDef(["task"], [new("todo", "Todo", StatusKind.Open)], []),
			]),
		]);
		var raw = JsonSerializer.Serialize(own, DefinitionJson);
		raw.Should().NotContain("autoWireSpecFrom");
		await SeedProjectBoard();
		await SeedInstanceRaw("own", raw);

		Migrator().Migrate().Should().Be(0);
		(await ReadInstanceJson("own")).Should().Be(raw, "left byte-for-byte untouched");
	}
}
