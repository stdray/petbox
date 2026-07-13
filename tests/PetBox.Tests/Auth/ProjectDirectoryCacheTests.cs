using LinqToDB;
using Microsoft.Extensions.Caching.Memory;
using PetBox.Core.Models;
using PetBox.Tests.Web;
using PetBox.Web.Auth;

namespace PetBox.Tests.Auth;

// The project cache inside ProjectDirectory (db-cache-behind-services). Every assertion about "hit
// the cache" is a COUNT of core.db opens on the counting factory — never a timing guess. The
// invalidation tests were verified RED first: with the cache.Remove calls in CreateAsync/DeleteAsync
// commented out, Create_then_read and Delete_then_read fail on the stale answer.
public sealed class ProjectDirectoryCacheTests
{
	static (ProjectDirectory Svc, CountingCoreDbFactory Dbf) New(TimeSpan? ttl = null)
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var dbf = new CountingCoreDbFactory(cs);
		return (new ProjectDirectory(dbf, new MemoryCache(new MemoryCacheOptions()), ttl), dbf);
	}

	static void Seed(CountingCoreDbFactory dbf, string ws, params string[] projects)
	{
		using var db = dbf.Open();
		db.Insert(new Workspace { Key = ws, Name = ws, Description = "", CreatedAt = DateTime.UtcNow });
		foreach (var p in projects)
			db.Insert(new Project { Key = p, WorkspaceKey = ws, Name = p, Description = "" });
	}

	[Fact]
	public async Task Repeated_reads_cost_zero_db_opens_once_warm()
	{
		var (svc, dbf) = New();
		Seed(dbf, "alpha", "app");

		// Warm every cached facet: the row (one open) and the workspace list (one open).
		(await svc.GetAsync("app")).Should().NotBeNull();
		await svc.ListAsync("alpha");

		dbf.Reset();
		(await svc.GetAsync("app"))!.Key.Should().Be("app");
		(await svc.BelongsAsync("app", "alpha")).Should().BeTrue();
		(await svc.BelongsAsync("app", "beta")).Should().BeFalse();
		(await svc.GetInWorkspaceAsync("alpha", "app")).Should().NotBeNull();
		(await svc.ExistsAsync("app")).Should().BeTrue();
		(await svc.ListAsync("alpha")).Should().ContainSingle(p => p.Key == "app");
		(await svc.CountAsync("alpha")).Should().Be(1);
		(await svc.ListByWorkspaceAsync(["alpha"])).Should().ContainKey("alpha");

		dbf.Opens.Should().Be(0, "every one of those answers must come from the cache");
		dbf.Statements.Should().Be(0);
	}

	[Fact]
	public async Task A_missing_project_is_never_cached_negatively()
	{
		var (svc, dbf) = New();
		Seed(dbf, "alpha");

		// Ask for a project that does not exist — the "no" must NOT be remembered...
		(await svc.GetAsync("late")).Should().BeNull();
		(await svc.BelongsAsync("late", "alpha")).Should().BeFalse();

		// ...because an out-of-band writer exists (WorkspaceMemory.EnsureContainerAsync inserts
		// container rows behind this service's back). A raw insert stands in for it here.
		using (var db = dbf.Open())
			db.Insert(new Project { Key = "late", WorkspaceKey = "alpha", Name = "late", Description = "" });

		(await svc.GetAsync("late")).Should().NotBeNull("a negative answer must be re-asked, never served stale");
		(await svc.BelongsAsync("late", "alpha")).Should().BeTrue();
	}

	[Fact]
	public async Task Create_then_read_sees_the_new_project_immediately()
	{
		var (svc, dbf) = New();
		Seed(dbf, "alpha", "app");

		// Warm the workspace list so the create has a stale cache to beat.
		(await svc.ListAsync("alpha")).Should().ContainSingle();
		await svc.ListByWorkspaceAsync(["alpha"]);

		var created = await svc.CreateAsync("alpha", "fresh", "Fresh", null);
		created.Should().BeOfType<ProjectChangeResult.Created>();

		(await svc.GetAsync("fresh")).Should().NotBeNull();
		(await svc.BelongsAsync("fresh", "alpha")).Should().BeTrue();
		(await svc.ListAsync("alpha")).Select(p => p.Key).Should().Equal("app", "fresh");
		(await svc.ListByWorkspaceAsync(["alpha"]))["alpha"].Select(p => p.Key).Should().Equal("app", "fresh");
		(await svc.CountAsync("alpha")).Should().Be(2);
	}

	[Fact]
	public async Task Delete_then_read_sees_the_project_gone_immediately()
	{
		var (svc, dbf) = New();
		Seed(dbf, "alpha", "app", "doomed");

		// Warm BOTH cached facets of the doomed project: its row and its workspace's list.
		(await svc.GetAsync("doomed")).Should().NotBeNull();
		(await svc.BelongsAsync("doomed", "alpha")).Should().BeTrue();
		(await svc.ListAsync("alpha")).Should().HaveCount(2);

		var deleted = await svc.DeleteAsync("alpha", "doomed");
		deleted.Should().BeOfType<ProjectChangeResult.Deleted>();

		(await svc.GetAsync("doomed")).Should().BeNull("the cached row must die with the project");
		(await svc.BelongsAsync("doomed", "alpha")).Should().BeFalse(
			"the binding filter asks this on every request — a stale yes would keep the route alive");
		(await svc.GetInWorkspaceAsync("alpha", "doomed")).Should().BeNull();
		(await svc.ListAsync("alpha")).Select(p => p.Key).Should().Equal("app");
		(await svc.ListByWorkspaceAsync(["alpha"]))["alpha"].Should().ContainSingle(p => p.Key == "app");
	}

	[Fact]
	public async Task Parallel_readers_of_one_key_agree_and_the_warm_phase_is_free()
	{
		var (svc, dbf) = New();
		Seed(dbf, "alpha", "app");

		// Cold: N concurrent misses may EACH load from the db (no single-flight lock — a lock held
		// across a db call is what this repo forbids), so the honest assertion is a bound, not 1.
		const int n = 32;
		var cold = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => svc.GetAsync("app")));
		cold.Should().OnlyContain(p => p != null && p.Key == "app" && p.WorkspaceKey == "alpha",
			"concurrent readers must never see a torn or wrong row");
		dbf.Opens.Should().BeLessThanOrEqualTo(n + 1, "a miss costs at most one open per concurrent reader");

		dbf.Reset();
		var warm = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => svc.GetAsync("app")));
		warm.Should().OnlyContain(p => p != null && p.Key == "app" && p.WorkspaceKey == "alpha");
		dbf.Opens.Should().Be(0, "once warm, concurrent readers are served from memory");
	}

	[Fact]
	public async Task The_ttl_backstop_heals_a_write_the_invalidation_cannot_see()
	{
		// A writer that does NOT go through ProjectDirectory (the class comment names the real ones:
		// WorkspaceAdminService's container cascade). The explicit invalidation cannot see it — the
		// TTL is the backstop that guarantees the stale positive dies on its own.
		var (svc, dbf) = New(ttl: TimeSpan.FromMilliseconds(200));
		Seed(dbf, "alpha", "app");

		(await svc.GetAsync("app")).Should().NotBeNull("warm the row cache");
		(await svc.ListAsync("alpha")).Should().HaveCount(1, "warm the list cache");

		using (var db = dbf.Open())
			await db.Projects.Where(p => p.Key == "app").DeleteAsync();

		await Task.Delay(500);

		(await svc.GetAsync("app")).Should().BeNull("the TTL must have expired the stale positive");
		(await svc.ListAsync("alpha")).Should().BeEmpty();
	}
}
