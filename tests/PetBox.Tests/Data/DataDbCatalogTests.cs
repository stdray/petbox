using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Data;
using PetBox.Data.Contract;
using PetBox.Data.Schema;
using PetBox.Data.Services;

namespace PetBox.Tests.Data;

// SERVICE-level proof of the DataDb name/quota rules. These rules used to live in the REST
// endpoint (PetBox.Data.DataDbsApi) and were enforced ONLY for HTTP callers; a DataDb name
// becomes a file name and a SQL identifier (identifiers cannot be parameterized), so the rules
// are the injection/path-traversal defence and must hold behind the catalog door for EVERY
// caller — REST, pages, MCP db_create. That is why these tests hit DataDbCatalog directly,
// with no web host in front of it: nothing here survives "because REST checked first".
public sealed class DataDbCatalogTests : IDisposable
{
	const string ProjectKey = "kpvotes";

	readonly string _baseDir;
	readonly ICoreDbFactory _core;
	readonly DataDbFactory _factory;
	readonly DataDbCatalog _catalog;

	public DataDbCatalogTests()
	{
		var cs = TestSchema.NewTempConnectionString("petbox-dbcatalog-test");
		TestSchema.Core(cs);
		_core = new CoreDbFactory(cs);

		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-dbcatalog-test-" + Guid.NewGuid().ToString("N"));
		_factory = new DataDbFactory(_baseDir);
		_catalog = new DataDbCatalog(_core, _factory);

		using var db = _core.Open();
		db.Insert(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
		db.Insert(new Project { Key = ProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
	}

	public void Dispose()
	{
		TestDirs.ClearPoolsUnder(_baseDir);
		TestDirs.CleanupOrDefer(_baseDir);
	}

	// ── name rules (moved from DataDbsApi — the injection/path-traversal defence) ──────────

	[Theory]
	[InlineData("Bad-Name")]      // uppercase
	[InlineData("9lead")]         // must start with a letter
	[InlineData("_lead")]         // '_' cannot lead
	[InlineData("has space")]
	[InlineData("has.dot")]
	[InlineData("../escape")]     // path traversal — the name becomes {baseDir}/{project}/{name}.db
	[InlineData("a\"; DROP TABLE x; --")] // identifier injection
	public async Task Create_RefusesNamesOutsideTheRegex(string name)
	{
		var result = await _catalog.CreateAsync(ProjectKey, name, null, null);

		var refused = result.Should().BeOfType<DataDbChangeResult.Refused>().Subject;
		refused.Reason.Should().Contain("invalid name",
			"the name regex (lowercase letter, then lowercase/digit/underscore/dash, 100 chars max) "
			+ "is enforced by the CATALOG, for every caller");
		File.Exists(Path.Combine(_baseDir, ProjectKey, $"{name}.db")).Should().BeFalse(
			"a refused name must never reach the file system");
	}

	[Fact]
	public async Task Create_RefusesNamesOver100Chars()
	{
		var name = "a" + new string('b', 100); // 101 chars; 100 is the regex maximum

		var result = await _catalog.CreateAsync(ProjectKey, name, null, null);

		result.Should().BeOfType<DataDbChangeResult.Refused>();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task Create_RefusesMissingName(string name)
	{
		var result = await _catalog.CreateAsync(ProjectKey, name, null, null);

		var refused = result.Should().BeOfType<DataDbChangeResult.Refused>().Subject;
		refused.Reason.Should().Be("name is required");
	}

	[Theory]
	[InlineData("__schema_versions")]
	[InlineData("__SCHEMA_VERSIONS")] // the reserved list is case-insensitive
	public async Task Create_RefusesReservedNames(string name)
	{
		// Today the regex already rejects the leading '_' before the reserved list is consulted;
		// the reserved rule is carried anyway (belt and braces on an injection surface), and this
		// test pins the OBSERVABLE rule: a reserved name is refused, whichever guard fires first.
		var result = await _catalog.CreateAsync(ProjectKey, name, null, null);

		result.Should().BeOfType<DataDbChangeResult.Refused>();
	}

	// ── project existence (moved from DataDbsApi) ──────────────────────────────────────────

	[Fact]
	public async Task Create_UnknownProject_NotFound()
	{
		var result = await _catalog.CreateAsync("no-such-project", "cache", null, null);

		result.Should().BeOfType<DataDbChangeResult.NotFound>(
			"petbox Projects is the source of truth — no DataDbs row may name a project that is not there");
		File.Exists(Path.Combine(_baseDir, "no-such-project", "cache.db")).Should().BeFalse();
	}

	// ── quota floor (moved from DataDbsApi) + default ───────────────────────────────────────

	[Fact]
	public async Task Create_RefusesQuotaBelowFloor()
	{
		var result = await _catalog.CreateAsync(ProjectKey, "cache", null, maxPageCount: 1023);

		var refused = result.Should().BeOfType<DataDbChangeResult.Refused>().Subject;
		refused.Reason.Should().Contain("maxPageCount must be >= 1024");
	}

	[Fact]
	public async Task Create_AcceptsQuotaAtTheFloor_AndDefaultsWhenOmitted()
	{
		var atFloor = await _catalog.CreateAsync(ProjectKey, "floor", null, maxPageCount: 1024);
		atFloor.Should().BeOfType<DataDbChangeResult.Created>()
			.Which.Db.MaxPageCount.Should().Be(1024);

		var defaulted = await _catalog.CreateAsync(ProjectKey, "defaulted", null, maxPageCount: null);
		defaulted.Should().BeOfType<DataDbChangeResult.Created>()
			.Which.Db.MaxPageCount.Should().Be(DataDbFactory.DefaultMaxPageCount);
	}

	// ── uniqueness of the (project, name) slot (was in BOTH the endpoint and the catalog) ──

	[Fact]
	public async Task Create_DuplicateSlot_Conflicts()
	{
		(await _catalog.CreateAsync(ProjectKey, "cache", null, null))
			.Should().BeOfType<DataDbChangeResult.Created>();

		var dup = await _catalog.CreateAsync(ProjectKey, "cache", null, null);

		dup.Should().BeOfType<DataDbChangeResult.Conflict>()
			.Which.Reason.Should().Contain("already exists");
	}

	[Fact]
	public async Task Create_PersistsRowAndFile()
	{
		var result = await _catalog.CreateAsync(ProjectKey, "cache", "vote cache", 4096);

		result.Should().BeOfType<DataDbChangeResult.Created>();
		File.Exists(_factory.GetDbPath(ProjectKey, "cache")).Should().BeTrue();
		using var db = _core.Open();
		var row = await db.DataDbs.FirstOrDefaultAsync(
			(DataDb d) => d.ProjectKey == ProjectKey && d.Name == "cache");
		row.Should().NotBeNull();
		row!.MaxPageCount.Should().Be(4096);
		row.Description.Should().Be("vote cache");
	}

	// ── migrations introspection (moved from SchemaApi.ListMigrationsAsync) ────────────────

	[Fact]
	public async Task ListMigrations_UnknownDb_IsNull_AndTheProjectIsPartOfTheAddress()
	{
		(await _catalog.ListMigrationsAsync(ProjectKey, "never-created")).Should().BeNull();

		// Cross-project: the db exists, but under ANOTHER project's address — same null,
		// so no project can introspect another project's file by naming it.
		await _catalog.CreateAsync(ProjectKey, "cache", null, null);
		using (var db = _core.Open())
			db.Insert(new Project { Key = "other", WorkspaceKey = "test", Name = "Other" });
		(await _catalog.ListMigrationsAsync("other", "cache")).Should().BeNull();
	}

	[Fact]
	public async Task ListMigrations_EmptyBeforeTheFirstMigration_ThenChronological()
	{
		await _catalog.CreateAsync(ProjectKey, "cache", null, null);

		// The journal table does not exist until the first migration creates it — empty, not an error.
		var before = await _catalog.ListMigrationsAsync(ProjectKey, "cache");
		before.Should().NotBeNull().And.BeEmpty();

		var runner = new SchemaRunner();
		await using (var conn = await _factory.OpenAsync(ProjectKey, "cache", DataDbFactory.DefaultMaxPageCount))
		{
			runner.Apply(conn, "M001", "CREATE TABLE a (id INTEGER)").Kind.Should().Be(SchemaApplyKind.Applied);
			runner.Apply(conn, "M002", "CREATE TABLE b (id INTEGER)").Kind.Should().Be(SchemaApplyKind.Applied);
		}

		var after = await _catalog.ListMigrationsAsync(ProjectKey, "cache");
		after.Should().NotBeNull().And.HaveCount(2);
		after!.Select(m => m.ScriptName).Should().ContainInOrder("M001", "M002");
		after.Should().BeInAscendingOrder(m => m.Id, "the journal id IS the chronology");
		after.All(m => m.Hash.Length > 0).Should().BeTrue();
	}

	// ── delete (address rule + row-then-file, unchanged but now the only path) ─────────────

	[Fact]
	public async Task Delete_UnknownDb_NotFound_AndDeleteRemovesRow()
	{
		(await _catalog.DeleteAsync(ProjectKey, "never-created"))
			.Should().BeOfType<DataDbChangeResult.NotFound>();

		await _catalog.CreateAsync(ProjectKey, "cache", null, null);
		TestDirs.ClearPoolsUnder(_baseDir); // release pooled handles so the file delete can succeed

		(await _catalog.DeleteAsync(ProjectKey, "cache"))
			.Should().BeOfType<DataDbChangeResult.Deleted>();
		using var db = _core.Open();
		(await db.DataDbs.AnyAsync((DataDb d) => d.ProjectKey == ProjectKey && d.Name == "cache"))
			.Should().BeFalse("the row goes immediately; the file is best-effort");
	}
}
