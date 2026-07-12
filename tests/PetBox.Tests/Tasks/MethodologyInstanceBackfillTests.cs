using System.Text.Json;
using System.Text.Json.Serialization;
using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
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
