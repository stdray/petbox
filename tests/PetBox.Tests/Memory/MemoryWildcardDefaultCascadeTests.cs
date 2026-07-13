using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Mcp;

namespace PetBox.Tests.Memory;

// The memory cascade is single-sourced on ModuleMcp.ResolveProject, so giving a cross-project
// ("*") key a DEFAULT project changes what an ABSENT projectKey means for it: it now cascades
// over the default project ⊕ that project's workspace container, exactly like a project-scoped
// key. A "*" key WITHOUT a default still degrades to nothing (bare memory_search must not throw).
public sealed class MemoryWildcardDefaultCascadeTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryService _memory;

	public MemoryWildcardDefaultCascadeTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memwild-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_memory = new MemoryService(new MemoryStore(_db.Factory(), _factory));
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	// A "*" key with a default: no projectKey anywhere — the writes land in the default project
	// (⊕ its workspace container) and the bare search cascades over both.
	[Fact]
	public async Task Wildcard_WithDefault_CascadesOverTheDefaultProjectAndItsWorkspace()
	{
		var http = Wildcard(defaultProject: Proj);

		var p = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "kafka rebalance storm", scope: "project");
		var w = await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "kafka rebalance storm", scope: "workspace");

		var res = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(), "kafka");

		res.Items.Select(h => h.Key).Should().Contain([p.Key, w.Key]);
		res.Items.Select(h => h.Scope).Should().Contain("project").And.Contain("workspace");
		// The project leg really is the DEFAULT project's container (not "*", which is never a
		// storage path).
		_db.MemoryStores.Select(s => s.ProjectKey).Should().Contain(Proj).And.NotContain(ProjectScope.AllProjects);
	}

	// The explicit arg still wins over the default — same key, another project.
	[Fact]
	public async Task Wildcard_WithDefault_ExplicitProjectKeyStillWins()
	{
		_db.Insert(new Project { Key = "other", WorkspaceKey = "ws", Name = "O", Description = "" });
		var http = Wildcard(defaultProject: Proj);

		await MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "kafka in other", projectKey: "other", scope: "project");

		var here = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(), "kafka",
			projectKey: "other", scope: "project");
		var dflt = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(), "kafka",
			scope: "project");

		here.Items.Should().ContainSingle();
		dflt.Items.Should().BeEmpty(); // the default project got nothing
	}

	// Unchanged behavior for a "*" key with NO default: bare search degrades to empty, and an
	// explicit scope=project throws (nothing resolves).
	[Fact]
	public async Task Wildcard_WithoutDefault_KeepsDegrading()
	{
		var http = Wildcard(defaultProject: null);

		var res = await MemoryTools.SearchAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, new NoopUsageRecorder(), "kafka");
		res.Items.Should().BeEmpty();

		var act = () => MemoryTools.RememberAsync(http, Flags(), _db.Factory().WorkspaceMemory(), _memory, "kafka", scope: "project");
		await act.Should().ThrowAsync<ArgumentException>();
	}

	static IHttpContextAccessor Wildcard(string? defaultProject)
	{
		var claims = new List<Claim>
		{
			new("project", ProjectScope.AllProjects),
			new("scopes", "memory:read,memory:write"),
		};
		if (defaultProject is not null)
			claims.Add(new Claim(ApiKeyAuthenticationHandler.DefaultProjectClaim, defaultProject));
		var id = new ClaimsIdentity(claims, "test");
		return new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = TestProjectCatalog.Services, User = new ClaimsPrincipal(id) } };
	}

	static FeatureFlags Flags()
	{
		var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
		{
			["Features:Memory"] = "true",
		}).Build();
		return new FeatureFlags(cfg);
	}
}
