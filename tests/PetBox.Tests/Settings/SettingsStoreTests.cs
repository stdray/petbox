using LinqToDB;
using LinqToDB.Async;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Tests.Settings;

// ISettingsStore is the DB half of settings: the scope cascade and the Settings table. It exists so
// SettingsResolver holds no factory (AGENTS.md — the database is visible only in the service layer),
// and so the resolver's per-property N+1 over the cascade collapses into ONE snapshot.
//
// SettingsResolverTests already covers the cascade end-to-end through the resolver. These tests
// cover what only the STORE can be asked: the shape of the chain it builds, and the one thing the
// single-statement row load could plausibly get wrong — see the phantom-pair test.
public sealed class SettingsStoreTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly SettingsStore _store;

	public SettingsStoreTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-setstore-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_store = new SettingsStore(_db.Factory());
	}

	public void Dispose()
	{
		_db.Dispose();
		try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
	}

	async Task SeedProjectAsync(string project, string workspace)
	{
		await _db.InsertAsync(new Workspace { Key = workspace, Name = workspace, Description = "", CreatedAt = DateTime.UtcNow });
		await _db.InsertAsync(new Project { Key = project, WorkspaceKey = workspace, Name = project, Description = "" });
	}

	Task SeedRowAsync(Scope scope, string scopeKey, string path, string value) =>
		_db.InsertAsync(new Setting
		{
			Scope = scope.ToString(),
			ScopeKey = scopeKey,
			Path = path,
			Type = "string",
			Value = value,
			UpdatedAt = DateTime.UtcNow,
		});

	[Fact]
	public async Task Chain_for_a_project_is_project_then_its_workspace_then_system()
	{
		await SeedProjectAsync("p1", "w1");

		var snap = await _store.LoadChainAsync(Scope.Project, "p1");

		// Deepest FIRST — the order is what makes "first row wins" mean "nearest override wins".
		snap.Chain.Should().Equal(
			(Scope.Project, "p1"),
			(Scope.Workspace, "w1"),
			(Scope.System, "$"));
	}

	[Fact]
	public async Task Chain_for_a_project_with_no_row_skips_the_workspace_link()
	{
		var snap = await _store.LoadChainAsync(Scope.Project, "ghost");

		snap.Chain.Should().Equal(
			(Scope.Project, "ghost"),
			(Scope.System, "$"));
	}

	[Fact]
	public async Task Chain_for_a_service_walks_project_then_workspace_then_system()
	{
		await SeedProjectAsync("p2", "w2");

		var snap = await _store.LoadChainAsync(Scope.Service, "p2/api");

		snap.Chain.Should().Equal(
			(Scope.Service, "p2/api"),
			(Scope.Project, "p2"),
			(Scope.Workspace, "w2"),
			(Scope.System, "$"));
	}

	[Fact]
	public async Task Chain_for_a_membership_splits_into_its_user_and_workspace()
	{
		var snap = await _store.LoadChainAsync(Scope.Membership, "7:w3");

		snap.Chain.Should().Equal(
			(Scope.Membership, "7:w3"),
			(Scope.User, "7"),
			(Scope.Workspace, "w3"),
			(Scope.System, "$"));
	}

	[Fact]
	public async Task Snapshot_carries_every_row_on_the_chain()
	{
		await SeedProjectAsync("p4", "w4");
		await SeedRowAsync(Scope.Project, "p4", "a", "from-project");
		await SeedRowAsync(Scope.Workspace, "w4", "b", "from-workspace");
		await SeedRowAsync(Scope.System, "$", "c", "from-system");

		var snap = await _store.LoadChainAsync(Scope.Project, "p4");

		snap.Find(Scope.Project, "p4", "a")!.Value.Should().Be("from-project");
		snap.Find(Scope.Workspace, "w4", "b")!.Value.Should().Be("from-workspace");
		snap.Find(Scope.System, "$", "c")!.Value.Should().Be("from-system");
		snap.Find(Scope.Project, "p4", "absent").Should().BeNull();
	}

	// THE regression this design could plausibly introduce. SQLite has no row-value IN, so the rows
	// are loaded with ONE statement whose WHERE is the CROSS PRODUCT of the chain's scopes and its
	// scope keys — `Scope IN (Project, Workspace, System) AND ScopeKey IN ('p5', 'w5', '$')`. That
	// predicate ALSO matches (Workspace, '$'), which is NOT a link of the chain: "$" is the System
	// key, and a row sitting at scope Workspace with key "$" is a different address entirely.
	//
	// If the exact-pair filter over the result were ever dropped, this row would leak into the
	// snapshot and win the cascade over the real System row below it. It must not be visible at all.
	[Fact]
	public async Task A_phantom_pair_from_the_cross_product_is_not_in_the_snapshot()
	{
		await SeedProjectAsync("p5", "w5");
		await SeedRowAsync(Scope.System, "$", "p", "the-real-system-value");
		await SeedRowAsync(Scope.Workspace, "$", "p", "phantom");   // matches the WHERE, is not on the chain

		var snap = await _store.LoadChainAsync(Scope.Project, "p5");

		snap.Find(Scope.Workspace, "$", "p").Should().BeNull("(Workspace, \"$\") is not a link of this chain");
		snap.Find(Scope.System, "$", "p")!.Value.Should().Be("the-real-system-value");
	}

	[Fact]
	public async Task Write_inserts_a_new_row_and_updates_an_existing_one()
	{
		await _store.WriteAsync(Scope.System, "$", [new SettingWrite("k", "string", "first")], updatedBy: 1);
		(await _store.LoadChainAsync(Scope.System, "$")).Find(Scope.System, "$", "k")!.Value.Should().Be("first");

		// Same address again: an UPDATE, not a second row.
		await _store.WriteAsync(Scope.System, "$", [new SettingWrite("k", "string", "second")], updatedBy: 2);

		(await _store.LoadChainAsync(Scope.System, "$")).Find(Scope.System, "$", "k")!.Value.Should().Be("second");
		(await _db.Settings.Where(s => s.Scope == "System" && s.ScopeKey == "$" && s.Path == "k").CountAsync())
			.Should().Be(1, "an upsert must not accumulate duplicate rows for one address");
	}

	[Fact]
	public async Task Write_of_several_properties_is_one_transaction()
	{
		await _store.WriteAsync(Scope.User, "u1",
			[new SettingWrite("x", "string", "1"), new SettingWrite("y", "string", "2")], updatedBy: null);

		var snap = await _store.LoadChainAsync(Scope.User, "u1");
		snap.Find(Scope.User, "u1", "x")!.Value.Should().Be("1");
		snap.Find(Scope.User, "u1", "y")!.Value.Should().Be("2");
	}

	[Fact]
	public async Task Write_of_nothing_touches_nothing()
	{
		await _store.WriteAsync(Scope.System, "$", [], updatedBy: null);

		(await _db.Settings.AsQueryable().CountAsync()).Should().Be(0);
	}

	[Fact]
	public async Task Delete_removes_the_override_and_leaves_the_rest_of_the_cascade()
	{
		await SeedProjectAsync("p6", "w6");
		await SeedRowAsync(Scope.Project, "p6", "k", "override");
		await SeedRowAsync(Scope.System, "$", "k", "default");

		await _store.DeleteAsync(Scope.Project, "p6", "k");

		var snap = await _store.LoadChainAsync(Scope.Project, "p6");
		snap.Find(Scope.Project, "p6", "k").Should().BeNull();
		snap.Find(Scope.System, "$", "k")!.Value.Should().Be("default", "deleting an override falls back up the cascade, it does not delete the default");
	}
}
