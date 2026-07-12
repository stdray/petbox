using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
	public async Task ProjectScopedClaim_NoArg_ResolvesToTheClaim() =>
		(await ModuleMcp.ResolveProject(Http("kpvotes"), null)).Should().Be("kpvotes");

	[Fact]
	public async Task Wildcard_WithDefault_NoArg_ResolvesToTheDefault() =>
		(await ModuleMcp.ResolveProject(Http(ProjectScope.AllProjects, "kpvotes"), null)).Should().Be("kpvotes");

	[Fact]
	public async Task Wildcard_WithoutDefault_NoArg_StillThrowsTheOldError()
	{
		var act = () => ModuleMcp.ResolveProject(Http(ProjectScope.AllProjects), null);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*projectKey is required*not scoped to a single project*");
	}

	[Fact]
	public async Task ExplicitArg_Wins_OverClaimAndOverDefault()
	{
		// project-scoped key: the arg must equal the claim (that's authz), but it is the arg
		// that is returned — the resolver never silently rewrites it.
		(await ModuleMcp.ResolveProject(Http("kpvotes"), "kpvotes")).Should().Be("kpvotes");
		// wildcard + default: the arg beats the default.
		(await ModuleMcp.ResolveProject(Http(ProjectScope.AllProjects, "kpvotes"), "other")).Should().Be("other");
	}

	[Fact]
	public async Task ProjectScopedClaim_ForeignArg_IsStillUnauthorized()
	{
		var act = () => ModuleMcp.ResolveProject(Http("kpvotes"), "other");
		await act.Should().ThrowAsync<UnauthorizedAccessException>();
	}

	// A default that does not authorize (only reachable if a project-scoped key somehow carried
	// one) must NOT smuggle access: a non-"*" claim ignores project_default entirely.
	[Fact]
	public async Task ProjectScopedClaim_IgnoresAStrayDefaultClaim() =>
		(await ModuleMcp.ResolveProject(Http("kpvotes", "other"), null)).Should().Be("kpvotes");

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
		var act = () => ApiKeyTools.CreateAsync(Admin(), _db.Factory(), "k", "memory:read",
			projectKey: "kpvotes", defaultProject: "kpvotes");
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*defaultProject is only valid with allProjects*");
	}

	[Fact]
	public async Task ApiKeyCreate_DefaultProject_MustExist()
	{
		var act = () => ApiKeyTools.CreateAsync(Admin(), _db.Factory(), "k", "memory:read",
			allProjects: true, defaultProject: "nope");
		await act.Should().ThrowAsync<InvalidOperationException>();
	}

	[Fact]
	public async Task ApiKeyCreate_AllProjectsWithDefault_PersistsAndIsListed()
	{
		var created = await ApiKeyTools.CreateAsync(Admin(), _db.Factory(), "wildcard", "memory:read",
			allProjects: true, defaultProject: "kpvotes");

		created.ProjectKey.Should().Be(ProjectScope.AllProjects);
		created.DefaultProjectKey.Should().Be("kpvotes");
		_db.ApiKeys.Single(k => k.Key == created.Key).DefaultProjectKey.Should().Be("kpvotes");

		var listed = await ApiKeyTools.ListAsync(Admin(), _db.Factory(), ProjectScope.AllProjects);
		listed.Keys.Single(k => k.Key == created.Key).DefaultProjectKey.Should().Be("kpvotes");
	}

	// A plain project-scoped key keeps a NULL default (it already defaults to its own claim).
	[Fact]
	public async Task ApiKeyCreate_ProjectScoped_LeavesTheDefaultNull()
	{
		var created = await ApiKeyTools.CreateAsync(Admin(), _db.Factory(), "scoped", "memory:read", projectKey: "kpvotes");
		created.DefaultProjectKey.Should().BeNull();
		_db.ApiKeys.Single(k => k.Key == created.Key).DefaultProjectKey.Should().BeNull();
	}

	// ── apikey_create sandboxOnly (spec work/smoke-writes-into-real-projects) ──

	// Minting a sandboxOnly key scoped to a SPECIFIC non-sandbox project would hand out a key that
	// can never write anything (ProjectScope.AuthorizesAsync refuses every call) — reject at mint
	// time rather than leave that silently useless.
	[Fact]
	public async Task ApiKeyCreate_SandboxOnly_AgainstANonSandboxProject_IsRejected()
	{
		var act = () => ApiKeyTools.CreateAsync(Admin(), _db.Factory(), "smoke-bad", "memory:read",
			projectKey: "kpvotes", sandboxOnly: true);
		(await act.Should().ThrowAsync<ArgumentException>())
			.WithMessage("*sandboxOnly*sandbox project*");
	}

	// …but scoped to a project that IS flagged sandbox, the mint succeeds and the flag persists.
	[Fact]
	public async Task ApiKeyCreate_SandboxOnly_AgainstASandboxProject_Succeeds()
	{
		_db.Insert(new Project { Key = "sandboxproj", WorkspaceKey = "ws", Name = "Sandbox", Sandbox = true });

		var created = await ApiKeyTools.CreateAsync(Admin(), _db.Factory(), "smoke-good", "memory:read",
			projectKey: "sandboxproj", sandboxOnly: true);

		created.SandboxOnly.Should().BeTrue();
		_db.ApiKeys.Single(k => k.Key == created.Key).SandboxOnly.Should().BeTrue();
	}

	// allProjects + sandboxOnly is valid BY DESIGN — one smoke key spanning every sandbox project.
	// The containment check then runs per-CALL (ProjectScope.AuthorizesAsync), not at mint time, so
	// there is no project to validate here.
	[Fact]
	public async Task ApiKeyCreate_SandboxOnly_WithAllProjects_IsAllowed_NoProjectToValidate()
	{
		var created = await ApiKeyTools.CreateAsync(Admin(), _db.Factory(), "smoke-wildcard", "memory:read",
			allProjects: true, sandboxOnly: true);

		created.ProjectKey.Should().Be(ProjectScope.AllProjects);
		created.SandboxOnly.Should().BeTrue();
		_db.ApiKeys.Single(k => k.Key == created.Key).SandboxOnly.Should().BeTrue();
	}

	// A plain (non-sandboxOnly) key keeps SandboxOnly false — no change to any existing mint.
	[Fact]
	public async Task ApiKeyCreate_Default_LeavesSandboxOnlyFalse()
	{
		var created = await ApiKeyTools.CreateAsync(Admin(), _db.Factory(), "plain", "memory:read", projectKey: "kpvotes");
		created.SandboxOnly.Should().BeFalse();
		_db.ApiKeys.Single(k => k.Key == created.Key).SandboxOnly.Should().BeFalse();
	}

	// ── plumbing ───────────────────────────────────────────────────────────────

	IHttpContextAccessor Http(string project, string? defaultProject = null, string scopes = "memory:read")
	{
		var claims = new List<Claim> { new("project", project), new("scopes", scopes) };
		if (defaultProject is not null)
			claims.Add(new Claim(ApiKeyAuthenticationHandler.DefaultProjectClaim, defaultProject));
		var id = new ClaimsIdentity(claims, "test");
		// ModuleMcp.AssertProject/ResolveProject resolve IProjectCatalog off the HttpContext's own
		// DI container (spec work/smoke-writes-into-real-projects) — none of these tests set the
		// `sandbox_only` claim, so the containment check short-circuits and this stub is never
		// actually queried, but it has to be resolvable or the DI lookup itself throws.
		var services = new ServiceCollection().AddSingleton<IProjectCatalog>(new ProjectCatalog(_db.Factory())).BuildServiceProvider();
		return new HttpContextAccessor
		{
			HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(id), RequestServices = services },
		};
	}

	IHttpContextAccessor Admin() => Http("$system", scopes: ApiKeyScopes.AdminProvision);
}
