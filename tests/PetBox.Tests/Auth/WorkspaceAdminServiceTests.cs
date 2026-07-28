using LinqToDB;
using Microsoft.Data.Sqlite;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Tests.Auth;

// IWorkspaceAdminService is the workspace catalog. The delete cascade is the load-bearing part: it
// must refuse a workspace that still holds USER projects, must NOT be blocked by the workspace's own
// memory container (which is a Projects row, and blocking on it made a fresh workspace undeletable),
// and must take the memberships with it — they are the owner's quota ledger.
public sealed class WorkspaceAdminServiceTests : IDisposable
{
	readonly List<string> _dirs = [];

	(WorkspaceAdminService Svc, IWorkspaceMembershipService Members, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		_dirs.Add(Path.GetDirectoryName(new SqliteConnectionStringBuilder(cs).DataSource)!);
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		var members = new WorkspaceMembershipService(dbf);
		var svc = new WorkspaceAdminService(
			dbf, new ProjectDirectory(dbf), members, new WorkspaceProvisioning(dbf, members));
		return (svc, members, dbf);
	}

	public void Dispose()
	{
		foreach (var dir in _dirs) TestDirs.CleanupOrDefer(dir);
	}

	static long SeedUser(ICoreDbFactory dbf, string name, int quota)
	{
		using var db = dbf.Open();
		return db.InsertWithInt64Identity(new User
		{
			Username = name,
			PasswordHash = "x",
			CreatedAt = DateTime.UtcNow,
			WorkspaceQuota = quota,
		});
	}

	[Fact]
	public async Task Create_provisions_the_container_and_the_creators_admin_role()
	{
		var (svc, members, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 1);

		var result = await svc.CreateAsync("alpha", "Alpha", "desc", uid, bypassQuota: false);

		result.Ok.Should().BeTrue();
		(await members.IsAdminAsync(uid, "alpha")).Should().BeTrue("spec workspace-creator-is-admin");

		var overview = await svc.GetOverviewAsync("alpha", includeContainers: true);
		// The memory container ships WITH the workspace — a workspace without one is a broken workspace.
		overview!.Projects.Select(p => p.Key).Should().Equal(["$ws-alpha"]);
		(await svc.GetOverviewAsync("alpha"))!.Projects.Should().BeEmpty("a container is not a user project");
		overview.MemberCount.Should().Be(1);

		// The allowance is spent — the quota is enforced inside the claim, not by whoever rendered a button.
		var second = await svc.CreateAsync("beta", "Beta", null, uid, bypassQuota: false);
		second.Ok.Should().BeFalse();
		second.Error.Should().Be(WorkspaceProvisioning.QuotaExhaustedMessage);
	}

	[Fact]
	public async Task Update_requires_a_name_and_404s_on_a_missing_workspace()
	{
		var (svc, _, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 1);
		await svc.CreateAsync("alpha", "Alpha", "", uid, bypassQuota: false);

		(await svc.UpdateAsync("alpha", " ", "x")).Should().BeOfType<WorkspaceChangeResult.Refused>();
		(await svc.UpdateAsync("nosuch", "N", "x")).Should().BeOfType<WorkspaceChangeResult.NotFound>();

		(await svc.UpdateAsync("alpha", "Renamed", "new desc")).Should().BeOfType<WorkspaceChangeResult.Changed>();
		var ws = await svc.GetAsync("alpha");
		ws!.Name.Should().Be("Renamed");
		ws.Description.Should().Be("new desc");
	}

	[Fact]
	public async Task Delete_refuses_system_and_a_workspace_that_still_holds_user_projects()
	{
		var (svc, _, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 1);
		await svc.CreateAsync("alpha", "Alpha", "", uid, bypassQuota: false);
		using (var db = dbf.Open())
			db.Insert(new Project { Key = "app", WorkspaceKey = "alpha", Name = "App", Description = "" });

		(await svc.DeleteAsync("$system")).Should().BeOfType<WorkspaceChangeResult.Refused>();

		(await svc.DeleteAsync("alpha")).Should().BeOfType<WorkspaceChangeResult.Refused>()
			.Which.Reason.Should().Contain("1 project(s)");
		(await svc.GetAsync("alpha")).Should().NotBeNull();
	}

	// The regression that was just fixed in the page and must not come back: the workspace's own
	// $ws-* container is a Projects row, so counting it as a project made a freshly created, entirely
	// empty workspace report "1 project(s)" and become undeletable by anyone — the container has no
	// delete button of its own.
	[Fact]
	public async Task Delete_cascades_the_container_and_the_memberships_of_an_empty_workspace()
	{
		var (svc, members, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 1);
		await svc.CreateAsync("alpha", "Alpha", "", uid, bypassQuota: false);

		(await svc.DeleteAsync("alpha")).Should().BeOfType<WorkspaceChangeResult.Changed>(
			"the container is the workspace's own belonging — it dies WITH it, it does not block it");

		(await svc.GetAsync("alpha")).Should().BeNull();
		(await members.CountMembersAsync("alpha")).Should().Be(0);
		(await members.CountOwnedWorkspacesAsync(uid)).Should().Be(0, "the allowance comes back — it is not a one-shot ticket");

		using var db = dbf.Open();
		db.Projects.Count(p => p.WorkspaceKey == "alpha").Should().Be(0, "the $ws-alpha container went with it");

		(await svc.DeleteAsync("alpha")).Should().BeOfType<WorkspaceChangeResult.NotFound>();
	}

	[Fact]
	public async Task List_returns_every_workspace_ordered_by_key()
	{
		var (svc, _, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 2);
		await svc.CreateAsync("beta", "B", "", uid, bypassQuota: false);
		await svc.CreateAsync("alpha", "A", "", uid, bypassQuota: false);

		(await svc.ListAsync()).Select(w => w.Key).Should().Equal("$system", "alpha", "beta");
		(await svc.GetOverviewAsync("nosuch")).Should().BeNull();
	}

	// The live installation's actual state (workspace `infra`, holding the `petbox` project itself,
	// has no Workspaces row) reproduced directly: a workspace created through the production path,
	// then its catalog row removed through a door OTHER than DeleteAsync (direct SQL — an incident
	// recovery, a pre-catalog migration; DeleteAsync itself is not exercised here on purpose, since
	// it refuses while the workspace still holds a user project). Its project and lazily-created
	// `$ws-<key>` container survive. ListAsync (the catalog's own word) drops it; the sysadmin page's
	// read must not.
	[Fact]
	public async Task ListForSysAdmin_includes_a_workspace_whose_catalog_row_is_gone_but_its_projects_remain()
	{
		var (svc, _, dbf) = New();
		var uid = SeedUser(dbf, "alice", quota: 1);
		(await svc.CreateAsync("infra", "Infra", "", uid, bypassQuota: false)).Ok.Should().BeTrue();
		using (var db = dbf.Open())
		{
			db.Insert(new Project { Key = "petbox", WorkspaceKey = "infra", Name = "PetBox", Description = "" });
			// Simulate the row vanishing through a door other than IWorkspaceAdminService.DeleteAsync —
			// that path is proven elsewhere to refuse while `petbox` (a user project) still lives here.
			await db.Workspaces.Where(w => w.Key == "infra").DeleteAsync();
		}

		(await svc.GetAsync("infra")).Should().BeNull("the catalog row is really gone");
		(await svc.ListAsync()).Select(w => w.Key).Should().NotContain("infra", "ListAsync is the catalog's own word");

		var forSysAdmin = await svc.ListForSysAdminAsync();
		forSysAdmin.Select(w => w.Key).Should().Contain("infra", "its projects prove it still exists");
		forSysAdmin.Select(w => w.Key).Should().Equal(["$system", "infra"],
			"a page counter counting THIS set must see it too");
	}
}
