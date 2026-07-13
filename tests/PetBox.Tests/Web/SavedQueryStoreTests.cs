using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Log.Core.Data;

namespace PetBox.Tests.Web;

// ISavedQueryStore is the door SavedQueries never had (db-out-of-pages-remaining-24): every read and
// write Pages/Logs/Index.cshtml.cs performs against that table now goes through here. Mirrors
// ProjectDirectoryTests's shape — a real core.db file behind a real CoreDbFactory, not a mock.
public sealed class SavedQueryStoreTests
{
	static (SavedQueryStore Svc, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		return (new SavedQueryStore(dbf), dbf);
	}

	[Fact]
	public async Task Save_creates_on_first_call_and_updates_in_place_on_the_second()
	{
		var (svc, dbf) = New();

		var created = await svc.SaveAsync("proj", "my query", "events | where Level == 'Error'");
		created.Name.Should().Be("my query");
		created.ProjectKey.Should().Be("proj");
		created.CreatedAt.Should().Be(created.UpdatedAt, "a fresh row has never been updated");

		var updated = await svc.SaveAsync("proj", "my query", "events | take 10");
		updated.Id.Should().Be(created.Id, "same (ProjectKey, Name) is an UPDATE, not a second row");
		updated.Kql.Should().Be("events | take 10");
		// BeCloseTo, not Be: created.CreatedAt is the in-memory DateTime.UtcNow (full tick precision);
		// SQLite round-trips a stored DateTime at millisecond precision, so the value SaveAsync reads
		// back on the update path loses sub-millisecond ticks against the value it was written with.
		updated.CreatedAt.Should().BeCloseTo(created.CreatedAt, TimeSpan.FromMilliseconds(2), "CreatedAt survives an update");

		(await svc.ListAsync("proj")).Should().ContainSingle();
	}

	[Fact]
	public async Task Save_trims_the_name_so_it_matches_on_the_next_call()
	{
		var (svc, _) = New();

		await svc.SaveAsync("proj", "  spaced  ", "events");
		var again = await svc.SaveAsync("proj", "spaced", "events | take 5");

		(await svc.ListAsync("proj")).Should().ContainSingle();
		again.Kql.Should().Be("events | take 5");
	}

	[Fact]
	public async Task List_and_Find_are_scoped_to_the_project()
	{
		var (svc, _) = New();

		await svc.SaveAsync("proja", "shared-name", "events | where A");
		await svc.SaveAsync("projb", "shared-name", "events | where B");

		(await svc.ListAsync("proja")).Select(q => q.Kql).Should().Equal("events | where A");
		(await svc.FindAsync("proja", "shared-name")).Should().NotBeNull().And.Match<SavedQuery?>(q => q!.Kql == "events | where A");
		(await svc.FindAsync("projb", "shared-name")).Should().NotBeNull().And.Match<SavedQuery?>(q => q!.Kql == "events | where B");
		(await svc.FindAsync("proja", "no-such-name")).Should().BeNull();
	}

	[Fact]
	public async Task Delete_is_scoped_to_the_project_the_id_alone_cannot_cross()
	{
		var (svc, _) = New();

		var victim = await svc.SaveAsync("projb", "victim", "events");

		// Same id, wrong project — must match nothing (the IDOR LogsSavedQueryAuthzTests guards
		// against at the HTTP layer; this pins the same guarantee at the door itself).
		await svc.DeleteAsync("proja", victim.Id);
		(await svc.FindAsync("projb", "victim")).Should().NotBeNull("a delete addressed to another project must not remove this row");

		await svc.DeleteAsync("projb", victim.Id);
		(await svc.FindAsync("projb", "victim")).Should().BeNull();
	}
}
