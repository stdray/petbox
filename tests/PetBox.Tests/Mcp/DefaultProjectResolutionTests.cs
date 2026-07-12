using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Mcp;

// ModuleMcp.ResolveProject is THE single resolver for every tool whose projectKey is OPTIONAL:
//     arg ?? (claim == "*" ? project_default claim : claim)
// A cross-project ("*") key's claim authorizes every project but names none — so it now falls
// back to the key's DefaultProjectKey (the `project_default` claim). Without one, the old error
// stands. An explicit projectKey always wins. whoami + apikey_create/list surface the field.
public sealed class DefaultProjectResolutionTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;

	public DefaultProjectResolutionTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-defproj-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = "kpvotes", WorkspaceKey = "ws", Name = "K", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// ── the resolver ───────────────────────────────────────────────────────────

	[Fact]
	public void ProjectScopedClaim_NoArg_ResolvesToTheClaim() =>
		ModuleMcp.ResolveProject(Http("kpvotes"), null).Should().Be("kpvotes");

	[Fact]
	public void Wildcard_WithDefault_NoArg_ResolvesToTheDefault() =>
		ModuleMcp.ResolveProject(Http(ProjectScope.AllProjects, "kpvotes"), null).Should().Be("kpvotes");

	[Fact]
	public void Wildcard_WithoutDefault_NoArg_StillThrowsTheOldError()
	{
		var act = () => ModuleMcp.ResolveProject(Http(ProjectScope.AllProjects), null);
		act.Should().Throw<ArgumentException>()
			.WithMessage("*projectKey is required*not scoped to a single project*");
	}

	[Fact]
	public void ExplicitArg_Wins_OverClaimAndOverDefault()
	{
		// project-scoped key: the arg must equal the claim (that's authz), but it is the arg
		// that is returned — the resolver never silently rewrites it.
		ModuleMcp.ResolveProject(Http("kpvotes"), "kpvotes").Should().Be("kpvotes");
		// wildcard + default: the arg beats the default.
		ModuleMcp.ResolveProject(Http(ProjectScope.AllProjects, "kpvotes"), "other").Should().Be("other");
	}

	[Fact]
	public void ProjectScopedClaim_ForeignArg_IsStillUnauthorized()
	{
		var act = () => ModuleMcp.ResolveProject(Http("kpvotes"), "other");
		act.Should().Throw<UnauthorizedAccessException>();
	}

	// A default that does not authorize (only reachable if a project-scoped key somehow carried
	// one) must NOT smuggle access: a non-"*" claim ignores project_default entirely.
	[Fact]
	public void ProjectScopedClaim_IgnoresAStrayDefaultClaim() =>
		ModuleMcp.ResolveProject(Http("kpvotes", "other"), null).Should().Be("kpvotes");

	// ── whoami ─────────────────────────────────────────────────────────────────

	[Fact]
	public void WhoAmI_SurfacesTheDefaultProject()
	{
		WhoAmITools.WhoAmI(Http(ProjectScope.AllProjects, "kpvotes")).DefaultProject.Should().Be("kpvotes");
		WhoAmITools.WhoAmI(Http(ProjectScope.AllProjects)).DefaultProject.Should().BeNull();
	}

	// ── apikey_create / apikey_list ────────────────────────────────────────────

	[Fact]
	public async Task ApiKeyCreate_DefaultProject_WithoutAllProjects_IsRejected()
	{
		var act = () => ApiKeyTools.CreateAsync(Admin(), _db, "k", "memory:read",
			projectKey: "kpvotes", defaultProject: "kpvotes");
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*defaultProject is only valid with allProjects*");
	}

	[Fact]
	public async Task ApiKeyCreate_DefaultProject_MustExist()
	{
		var act = () => ApiKeyTools.CreateAsync(Admin(), _db, "k", "memory:read",
			allProjects: true, defaultProject: "nope");
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task ApiKeyCreate_AllProjectsWithDefault_PersistsAndIsListed()
	{
		var created = await ApiKeyTools.CreateAsync(Admin(), _db, "wildcard", "memory:read",
			allProjects: true, defaultProject: "kpvotes");

		created.ProjectKey.Should().Be(ProjectScope.AllProjects);
		created.DefaultProjectKey.Should().Be("kpvotes");
		_db.ApiKeys.Single(k => k.Key == created.Key).DefaultProjectKey.Should().Be("kpvotes");

		var listed = await ApiKeyTools.ListAsync(Admin(), _db, ProjectScope.AllProjects);
		listed.Keys.Single(k => k.Key == created.Key).DefaultProjectKey.Should().Be("kpvotes");
	}

	// A plain project-scoped key keeps a NULL default (it already defaults to its own claim).
	[Fact]
	public async Task ApiKeyCreate_ProjectScoped_LeavesTheDefaultNull()
	{
		var created = await ApiKeyTools.CreateAsync(Admin(), _db, "scoped", "memory:read", projectKey: "kpvotes");
		created.DefaultProjectKey.Should().BeNull();
		_db.ApiKeys.Single(k => k.Key == created.Key).DefaultProjectKey.Should().BeNull();
	}

	// ── plumbing ───────────────────────────────────────────────────────────────

	static IHttpContextAccessor Http(string project, string? defaultProject = null, string scopes = "memory:read")
	{
		var claims = new List<Claim> { new("project", project), new("scopes", scopes) };
		if (defaultProject is not null)
			claims.Add(new Claim(ApiKeyAuthenticationHandler.DefaultProjectClaim, defaultProject));
		var id = new ClaimsIdentity(claims, "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id) } };
	}

	static IHttpContextAccessor Admin() => Http("$system", scopes: ApiKeyScopes.AdminProvision);
}
