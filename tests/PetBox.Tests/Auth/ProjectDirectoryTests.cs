using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Auth;

namespace PetBox.Tests.Auth;

// IProjectDirectory is the catalog of projects: the IDOR answer (BelongsAsync / GetInWorkspaceAsync),
// the container filter that no page may re-derive, and the create/delete rules.
public sealed class ProjectDirectoryTests
{
	static (ProjectDirectory Svc, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		return (new ProjectDirectory(dbf), dbf);
	}

	static void SeedWorkspace(ICoreDbFactory dbf, string key)
	{
		using var db = dbf.Open();
		db.Insert(new Workspace { Key = key, Name = key, Description = "", CreatedAt = DateTime.UtcNow });
	}

	static void SeedProject(ICoreDbFactory dbf, string key, string ws)
	{
		using var db = dbf.Open();
		db.Insert(new Project { Key = key, WorkspaceKey = ws, Name = key, Description = "" });
	}

	[Fact]
	public async Task Belongs_and_GetInWorkspace_are_blind_to_another_tenants_project()
	{
		var (svc, dbf) = New();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "alpha");

		(await svc.BelongsAsync("app", "alpha")).Should().BeTrue();
		(await svc.BelongsAsync("app", "beta")).Should().BeFalse();
		(await svc.GetInWorkspaceAsync("beta", "app")).Should().BeNull(
			"a foreign project is indistinguishable from a missing one — no existence oracle");
		(await svc.GetInWorkspaceAsync("alpha", "app")).Should().NotBeNull();
		(await svc.ExistsAsync("app")).Should().BeTrue();
		(await svc.ExistsAsync("nosuch")).Should().BeFalse();
	}

	[Fact]
	public async Task List_hides_the_workspace_memory_container_unless_asked()
	{
		var (svc, dbf) = New();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "$ws-alpha", "alpha");

		(await svc.ListAsync("alpha")).Select(p => p.Key).Should().Equal("app");
		(await svc.ListAsync("alpha", includeContainers: true)).Select(p => p.Key).Should().Equal("$ws-alpha", "app");
		(await svc.CountAsync("alpha")).Should().Be(1, "the container is not a project a human made");
		(await svc.CountAsync("alpha", includeContainers: true)).Should().Be(2);
	}

	[Fact]
	public async Task ListByWorkspace_groups_the_whole_sidebar_in_one_read()
	{
		var (svc, dbf) = New();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "$ws-alpha", "alpha");
		SeedProject(dbf, "site", "beta");

		var byWs = await svc.ListByWorkspaceAsync(["alpha", "beta"]);

		byWs["alpha"].Select(p => p.Key).Should().Equal("app");
		byWs["beta"].Select(p => p.Key).Should().Equal("site");
		(await svc.ListByWorkspaceAsync([])).Should().BeEmpty();
	}

	[Fact]
	public async Task Create_enforces_every_key_rule_in_one_place()
	{
		var (svc, dbf) = New();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "taken", "alpha");

		(await svc.CreateAsync("alpha", "", "Name", null)).Should().BeOfType<ProjectChangeResult.Refused>();
		(await svc.CreateAsync("$system", "app", "App", null)).Should().BeOfType<ProjectChangeResult.Refused>()
			.Which.Reason.Should().Contain("$system");
		(await svc.CreateAsync("alpha", "admin", "App", null)).Should().BeOfType<ProjectChangeResult.Refused>()
			.Which.Reason.Should().Contain("reserved");
		(await svc.CreateAsync("alpha", "$ws-alpha", "App", null)).Should().BeOfType<ProjectChangeResult.Refused>()
			.Which.Reason.Should().Contain("reserved");
		(await svc.CreateAsync("alpha", "taken", "App", null)).Should().BeOfType<ProjectChangeResult.Refused>()
			.Which.Reason.Should().Contain("already exists");

		var created = (await svc.CreateAsync("alpha", "app", "App", "desc"))
			.Should().BeOfType<ProjectChangeResult.Created>().Subject;
		created.Project.WorkspaceKey.Should().Be("alpha");
		(await svc.ListAsync("alpha")).Select(p => p.Key).Should().Equal("app", "taken");
	}

	[Fact]
	public async Task Delete_refuses_the_reserved_builtins_and_a_cross_workspace_address()
	{
		var (svc, dbf) = New();
		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "alpha");

		(await svc.DeleteAsync("$system", "$system")).Should().BeOfType<ProjectChangeResult.Refused>();

		(await svc.DeleteAsync("beta", "app")).Should().BeOfType<ProjectChangeResult.NotFound>(
			"the workspace is part of the ADDRESS — a forged POST from another tenant deletes nothing");
		(await svc.ExistsAsync("app")).Should().BeTrue();

		(await svc.DeleteAsync("alpha", "app")).Should().BeOfType<ProjectChangeResult.Deleted>();
		(await svc.ExistsAsync("app")).Should().BeFalse();
	}
}
