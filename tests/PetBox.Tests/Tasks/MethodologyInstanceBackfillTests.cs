using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Tasks.Data;
using PetBox.Tasks.Services;
using PetBox.Tasks.Workflow;

namespace PetBox.Tests.Tasks;

// methodology-instance-backfill: every existing board gets exactly-one membership;
// quartet boards share one instance; loose boards share a coherent group; idempotent.
public sealed class MethodologyInstanceBackfillTests : IDisposable
{
	const string Proj = "proj";
	const string ProjB = "proj-b";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;
	readonly TaskBoardStore _boards;
	readonly TasksService _tasks;

	static readonly JsonSerializerOptions DefinitionJson = new(JsonSerializerDefaults.Web)
	{
		Converters = { new JsonStringEnumConverter() },
	};

	public MethodologyInstanceBackfillTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mibf-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_db.Insert(new Project { Key = ProjB, WorkspaceKey = "ws", Name = "B", Description = "" });
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

	MethodologyInstanceBackfill Backfill() => new(_db.Factory(), _factory);

	// Seed a legacy board (null membership) the way pre-instance catalogs look.
	async Task<TaskBoardMeta> SeedBoard(string project, string name, string kind, DateTime? closedAt = null)
	{
		var meta = await _boards.CreateAsync(project, name, description: null, kind: kind);
		if (closedAt is not null)
			await _boards.UpdateAsync(project, name, m => m with { ClosedAt = closedAt });
		// CreateAsync leaves MethodologyInstance null when not passed — assert the legacy shape.
		var row = (await _boards.FindAsync(project, name))!;
		row.MethodologyInstance.Should().BeNull();
		return row;
	}

	[Fact]
	public async Task QuartetBoards_ShareOneInstance_FromBuiltinRules()
	{
		await SeedBoard(Proj, "intake", "intake");
		await SeedBoard(Proj, "ideas", "ideas");
		await SeedBoard(Proj, "spec", "spec");
		await SeedBoard(Proj, "work", "work");

		Backfill().Migrate().Should().Be(1);

		var boards = await _boards.ListAsync(Proj);
		boards.Should().HaveCount(4);
		boards.Should().OnlyContain(b => b.MethodologyInstance == "quartet");

		var inst = await _tasks.GetMethodologyInstanceAsync(Proj, "quartet");
		inst.Should().NotBeNull();
		inst!.Closed.Should().BeFalse();
		inst.Kinds.Should().BeEquivalentTo(["intake", "ideas", "spec", "work"]);
		inst.Boards.Select(b => b.Name).Should().BeEquivalentTo(["intake", "ideas", "spec", "work"]);
	}

	[Fact]
	public async Task ProjectDef_BecomesMainInstance_AllBoardsJoin()
	{
		// Store a singleton def, then seed mixed boards (process-role + simple).
		var def = MethodologyPresets.RenderBuiltinTemplate("quartet") with { Name = "custom-flow" };
		using (var ctx = _factory.NewEnsuredConnection(Proj))
		{
			await TemporalStore.UpsertAsync(ctx, new[]
			{
				new MethodologyDefRow
				{
					Key = MethodologyDefRow.SingletonKey,
					Version = 0,
					Json = JsonSerializer.Serialize(def, DefinitionJson),
				},
			});
		}
		await SeedBoard(Proj, "intake", "intake");
		await SeedBoard(Proj, "work", "work");
		await SeedBoard(Proj, "scratch", "simple");

		Backfill().Migrate().Should().Be(1);

		var boards = await _boards.ListAsync(Proj);
		boards.Should().OnlyContain(b => b.MethodologyInstance == "main");
		var inst = await _tasks.GetMethodologyInstanceAsync(Proj, "main");
		inst.Should().NotBeNull();
		inst!.DefinitionName.Should().Be("custom-flow");
		inst.Boards.Should().HaveCount(3);
	}

	[Fact]
	public async Task ClassicAndSimple_ShareOneLooseInstance()
	{
		// Choice documented in MethodologyInstanceBackfill: all loose boards share one
		// coherent instance (classic|simple unlimited) — not one instance per board.
		await SeedBoard(Proj, "board-a", "classic");
		await SeedBoard(Proj, "board-b", "classic");
		await SeedBoard(Proj, "notes", "simple");

		Backfill().Migrate().Should().Be(1);

		var boards = await _boards.ListAsync(Proj);
		boards.Should().OnlyContain(b => b.MethodologyInstance == "classic");
		(await _tasks.ListMethodologyInstancesAsync(Proj)).Should().ContainSingle(i => i.Name == "classic");
	}

	[Fact]
	public async Task DuplicateOpenProcessRole_OpensSecondInstance()
	{
		await SeedBoard(Proj, "work", "work");
		await SeedBoard(Proj, "work-extra", "work");
		await SeedBoard(Proj, "spec", "spec");

		Backfill().Migrate().Should().Be(1);

		var boards = await _boards.ListAsync(Proj);
		var byName = boards.ToDictionary(b => b.Name, b => b.MethodologyInstance!);
		// First open work + spec share primary; second open work needs its own instance.
		byName["work"].Should().Be("quartet");
		byName["spec"].Should().Be("quartet");
		byName["work-extra"].Should().Be("quartet-2");

		var list = await _tasks.ListMethodologyInstancesAsync(Proj);
		list.Select(i => i.Name).Should().BeEquivalentTo(["quartet", "quartet-2"]);
	}

	[Fact]
	public async Task ClosedProcessRole_JoinsPrimary_WithoutConsumingSlot()
	{
		await SeedBoard(Proj, "work-old", "work", closedAt: DateTime.UtcNow.AddDays(-1));
		await SeedBoard(Proj, "work", "work");
		await SeedBoard(Proj, "spec", "spec");

		Backfill().Migrate().Should().Be(1);

		var boards = await _boards.ListAsync(Proj);
		boards.Should().OnlyContain(b => b.MethodologyInstance == "quartet");
		(await _tasks.ListMethodologyInstancesAsync(Proj)).Should().ContainSingle();
	}

	[Fact]
	public async Task MultiProject_AllBoardsAssigned_IdempotentRerun()
	{
		await SeedBoard(Proj, "intake", "intake");
		await SeedBoard(Proj, "ideas", "ideas");
		await SeedBoard(Proj, "spec", "spec");
		await SeedBoard(Proj, "work", "work");
		await SeedBoard(ProjB, "todo", "classic");
		await SeedBoard(ProjB, "bag", "simple");

		var first = Backfill().Migrate();
		first.Should().Be(2);

		// Exactly-one membership everywhere.
		foreach (var p in new[] { Proj, ProjB })
		{
			var boards = await _boards.ListAsync(p);
			boards.Should().OnlyContain(b => !string.IsNullOrWhiteSpace(b.MethodologyInstance));
		}

		// Idempotent: second pass touches nothing.
		Backfill().Migrate().Should().Be(0);
		// Memberships unchanged.
		(await _boards.ListAsync(Proj)).Should().OnlyContain(b => b.MethodologyInstance == "quartet");
		(await _boards.ListAsync(ProjB)).Should().OnlyContain(b => b.MethodologyInstance == "classic");
	}

	[Fact]
	public async Task AdoptsIntoExistingOpenInstance_WhenSlotsFree()
	{
		// Simulate a post-enable project that still has a legacy unassigned simple board.
		await _tasks.CreateMethodologyInstanceAsync(Proj, "quartet", "builtin", "quartet");
		await SeedBoard(Proj, "scratch", "simple");

		Backfill().Migrate().Should().Be(1);

		var scratch = await _boards.FindAsync(Proj, "scratch");
		scratch!.MethodologyInstance.Should().Be("quartet");
		// No extra instance minted.
		(await _tasks.ListMethodologyInstancesAsync(Proj)).Should().ContainSingle(i => i.Name == "quartet");
	}

	// FORK pin (stage2/dup-arrays axis-2, item B — TasksService.ApplyWorkflow's
	// `runtime.PresetKind(kindSlug) == BoardKind.Simple` door gate): the gate only fires while
	// the board's runtime is PresetsOnly (never backfilled, or backfilled into an instance
	// whose kinds don't declare "simple" verbatim). A LONE simple board — no process-role or
	// classic siblings — backfills into an instance whose rules MATERIALIZE "simple" as a
	// DECLARED kind (MethodologyPresets.RenderBuiltinTemplate("simple")), so
	// `runtime.IsDefinedKind("simple")` flips true and `PresetKind(...)` reads null forever
	// after (never == BoardKind.Simple again) — the same shape as the documented spec/
	// PresetKind production regression IsSpecKind fixed. An invalid type is REJECTED on both
	// sides either way (Merge already lowercases Type before ApplyWorkflow runs, so this is
	// NOT a case-sensitivity gap) — but via two DIFFERENT code paths with two DIFFERENT
	// ArgumentException messages: the explicit door gate's "invalid type 'x' for a simple
	// board; valid: ..." vs. runtime.For() returning null and WorkflowEngine.Validate's generic
	// "board kind 'simple' needs a known type (...); got 'x'". Replacing the enum comparison
	// with an IsSpecKind-style `IsSimpleKind` data predicate (KindName == "simple" for declared
	// AND preset kinds alike) would make the door gate ALSO fire in the materialized case,
	// collapsing this message onto the door gate's wording — an observable (if minor) contract
	// change, not a pure refactor. No MethodologyKindDef field distinguishes "needs the
	// explicit door-side default+vocabulary check" from any other preset kind, and minting one
	// is against the closed-spec instruction, so item B is left AS-IS; these two tests pin
	// today's divergent message on both sides of the fork.
	[Fact]
	public async Task Simple_PresetsOnly_InvalidType_RejectedByTheExplicitDoorGate()
	{
		// Never backfilled: the board stays on the PresetsOnly runtime, so
		// `PresetKind(kindSlug) == BoardKind.Simple` is true and TasksService.ApplyWorkflow's
		// explicit door gate runs BEFORE runtime.For() ever resolves a workflow.
		await SeedBoard(Proj, "scratch", "simple");
		var act = () => _tasks.UpsertAsync(Proj, "scratch", new[]
		{
			new NodePatch { Key = "n1", Title = "N1", Body = "b", Type = "zzz" },
		});
		var ex = await act.Should().ThrowAsync<ArgumentException>();
		ex.Which.Message.Should().Contain("invalid type 'zzz' for a simple board");
	}

	[Fact]
	public async Task Simple_AfterBackfillMaterializesTheKind_InvalidType_RejectedByWorkflowEngineInstead()
	{
		// ProjB's "bag" is the ONLY board in its project — no process-role/classic siblings —
		// so backfill's ResolvePrimaryRules picks the "simple" builtin template, whose rendered
		// definition DECLARES "simple" as a kind. From then on `IsDefinedKind("simple")` is
		// true, `PresetKind("simple")` reads null, and the door gate at TasksService.cs never
		// fires again for this board — the STILL-invalid type is instead caught by
		// runtime.For() returning null (no matching block) and WorkflowEngine.Validate's
		// generic "needs a known type" refusal. Same outcome (rejected), different message.
		await SeedBoard(ProjB, "bag", "simple");
		Backfill().Migrate().Should().Be(1);
		(await _boards.FindAsync(ProjB, "bag"))!.MethodologyInstance.Should().Be("simple");

		var act = () => _tasks.UpsertAsync(ProjB, "bag", new[]
		{
			new NodePatch { Key = "n1", Title = "N1", Body = "b", Type = "zzz" },
		});
		var ex = await act.Should().ThrowAsync<ArgumentException>();
		ex.Which.Message.Should().Contain("needs a known type");
		ex.Which.Message.Should().NotContain("for a simple board"); // NOT the door gate's wording
	}

	[Fact]
	public async Task AlreadyAssigned_BoardsUntouched()
	{
		await _tasks.CreateMethodologyInstanceAsync(Proj, "main", "builtin", "classic");
		var before = await _boards.ListAsync(Proj);
		before.Should().OnlyContain(b => b.MethodologyInstance == "main");

		Backfill().Migrate().Should().Be(0);
		var after = await _boards.ListAsync(Proj);
		after.Should().BeEquivalentTo(before);
	}
}
