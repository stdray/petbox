using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Data.Temporal;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Data;

namespace PetBox.Tests.Tasks;

// The one-time spec-flat-tags back-fill: legacy "phase/wave/task" keys become flat slugs,
// the old nesting becomes part_of edges, and same-slug collisions across branches get a
// stable suffix. Idempotent.
public sealed class FlatNodePartOfMigratorTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<TasksDb> _factory;

	public FlatNodePartOfMigratorTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-flatmig-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
			c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	[Fact]
	public async Task Migrate_FlattensKeys_SynthesizesPartOf_AndSuffixesCollisions()
	{
		var ctx = _factory.GetDb(Proj);
		await TemporalStore.UpsertAsync(ctx, new[]
		{
			Node("spec", "auth", "id-auth"),
			Node("spec", "auth/login", "id-login"),
			Node("spec", "auth/login/mfa", "id-mfa"),
			Node("spec", "ui", "id-ui"),
			Node("spec", "ui/login", "id-uilogin"), // last segment "login" collides with auth/login
		}, partition: n => n.Board == "spec");

		var relations = new RelationStore(_factory);
		var migrated = new FlatNodePartOfMigrator(Path.Combine(_dir, "tasks"), _factory, relations).Migrate();
		migrated.Should().Be(1);

		// Keys flattened; the second "login" gets a deterministic suffix.
		var keys = _factory.GetDb(Proj).PlanNodes.Where(n => n.Board == "spec" && n.ActiveTo == null)
			.Select(n => n.Key).ToList();
		keys.Should().Contain(["auth", "login", "mfa", "ui"]);
		keys.Should().Contain(k => k.StartsWith("login-", StringComparison.Ordinal));
		keys.Should().NotContain(k => k.Contains('/'));

		// part_of edges preserve the old nesting (bound to stable NodeIds).
		var edges = await relations.ListByKindAsync(Proj, "part_of");
		edges.Should().HaveCount(3);
		edges.Should().ContainSingle(e => e.FromNodeId == "id-login" && e.ToNodeId == "id-auth");
		edges.Should().ContainSingle(e => e.FromNodeId == "id-mfa" && e.ToNodeId == "id-login");
		edges.Should().ContainSingle(e => e.FromNodeId == "id-uilogin" && e.ToNodeId == "id-ui");

		// Idempotent: a rerun converts nothing (no '/' keys remain).
		new FlatNodePartOfMigrator(Path.Combine(_dir, "tasks"), _factory, relations).Migrate().Should().Be(0);
		(await relations.ListByKindAsync(Proj, "part_of")).Should().HaveCount(3);
	}

	static PlanNode Node(string board, string key, string nodeId) => new()
	{
		Board = board,
		Key = key,
		NodeId = nodeId,
		Version = 0,
		Status = "defined",
		Type = "spec",
		Name = key,
		Body = "x",
	};
}
