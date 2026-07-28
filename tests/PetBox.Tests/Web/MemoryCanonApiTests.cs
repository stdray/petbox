using System.Net;
using System.Net.Http.Json;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Web.Memory;

namespace PetBox.Tests.Web;

// Shared per-class host for MemoryCanonApiTests (xUnit news the test class per test, so
// without this fixture every test boots its own WebApplicationFactory). Per-test isolation
// comes from ResetAsync: the memory store files under the test's baseDir are deleted (one
// test seeds canon entries in both scopes while another asserts both scopes are EMPTY), and
// the X-Api-Key default header tests add to the shared client is stripped (one test relies
// on its absence for the 401 branch).
public sealed class MemoryCanonApiFixture : IAsyncLifetime
{
	public const string TestProjectKey = "kpvotes";
	public const string TestApiKey = "yb_key_test_canon_xyz";

	// A SANDBOX project in the SAME workspace, and a sandboxOnly key that legitimately owns it. This
	// pair is the shape that made the canon route leak: the key is entitled to the project, the PEP
	// allows it, and the handler then derived the workspace container the PEP never judged. Until
	// work `memory-container-sandbox-containment-bypass` this file contained the word "sandbox" zero
	// times, so the route's entire behaviour under a contained key was unmeasured.
	public const string SandboxProjectKey = "sbox";
	public const string SandboxApiKey = "yb_key_test_canon_sandbox";

	readonly string _baseDir;

	public WebApplicationFactory<Program> Factory { get; }
	public HttpClient Client { get; private set; } = null!;

	public MemoryCanonApiFixture()
	{
		_baseDir = Path.Combine(Path.GetTempPath(), "petbox-canon-test-" + Guid.NewGuid().ToString("N"));
		Environment.SetEnvironmentVariable("PETBOX_MASTER_KEY", "test-key-for-secrets");

		Factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString(),
						["Host:BackgroundServices"] = "false",
						["Features:Memory"] = "true",
					});
				});
				b.ConfigureServices(svc =>
				{
					// Isolate the memory store files under a per-test temp dir (mirrors the
					// IDataDbFactory override in QueryExecApiTests).
					var existing = svc.SingleOrDefault(d => d.ServiceType == typeof(IScopedDbFactory<MemoryDb>));
					if (existing is not null) svc.Remove(existing);
					svc.AddSingleton<IScopedDbFactory<MemoryDb>>(_ => new ScopedDbFactory<MemoryDb>(
						Path.Combine(_baseDir, "memory"), Scope.Project,
						cs => new MemoryDb(MemoryDb.CreateOptions(cs)), TestSchema.Memory));
				});
			});
	}

	public async ValueTask InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs); // runs migrations: seeds $system + $workspace projects
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		await db.ApiKeys.Where(k => k.Key == TestApiKey || k.Key == SandboxApiKey).DeleteAsync();
		await db.Projects.Where(p => p.Key == TestProjectKey || p.Key == SandboxProjectKey).DeleteAsync();
		await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

		await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
		await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "memory:read,memory:write", CreatedAt = DateTime.UtcNow });

		await db.InsertAsync(new Project { Key = SandboxProjectKey, WorkspaceKey = "test", Name = "Sandbox", Sandbox = true });
		await db.InsertAsync(new ApiKey
		{
			Key = SandboxApiKey,
			ProjectKey = SandboxProjectKey,
			SandboxOnly = true,
			Scopes = "memory:read,memory:write",
			CreatedAt = DateTime.UtcNow,
		});
	}

	// Per-test reset under the shared host: strip the auth header a previous test added to
	// the shared client, and delete the per-scope memory store files a previous test seeded
	// (pool handles released first).
	public async Task ResetAsync()
	{
		Client.DefaultRequestHeaders.Remove("X-Api-Key");

		// The factory caches per (scope, store) — evict the canon store of both scopes so
		// the cached contexts release their file handles before the deletes below.
		// Test project lives in workspace "test" → container is "$ws-test" (not global $workspace).
		var memFactory = Factory.Services.GetRequiredService<IScopedDbFactory<MemoryDb>>();
		await memFactory.EvictAsync(TestProjectKey, "canon");
		await memFactory.EvictAsync("$ws-test", "canon");
		await memFactory.EvictAsync("$workspace", "canon");
		await memFactory.EvictAsync(SandboxProjectKey, "canon");
		if (!Directory.Exists(_baseDir)) return;
		TestDirs.ClearPoolsUnder(_baseDir);
		foreach (var file in Directory.EnumerateFiles(_baseDir, "*.db", SearchOption.AllDirectories))
			if (!PetBox.Core.Data.ScopedDbFiles.TryDelete(file))
				throw new InvalidOperationException($"per-test reset could not delete {file} (still locked)");
	}

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

// GET /api/memory/{projectKey}/canon (spec agent-wiring, memory-canon-storage): the wiring-hook
// read surface for the curated memory canon. Returns the project's canon index and the shared
// workspace canon index; an EMPTY queried scope carries an empty Body at Version 0 (still 200),
// not a bare null — null is reserved for a leg never asked (no workspace) or withheld (sandbox
// containment); no key is 401.
//
// Card canon-banner-empty-notice-unlabelled: the empty leg's Body used to carry a fixed
// human-readable nudge string. It is now "" — Version 0 alone is the discriminator; the
// human-readable text is synthesized client-side (canon.ts's EMPTY_CANON_TEXT), attributed to
// the specific leg under its own heading, never taken from this wire response.
public sealed class MemoryCanonApiTests : IClassFixture<MemoryCanonApiFixture>, IAsyncLifetime
{
	const string TestProjectKey = MemoryCanonApiFixture.TestProjectKey;
	const string TestApiKey = MemoryCanonApiFixture.TestApiKey;
	const string SandboxProjectKey = MemoryCanonApiFixture.SandboxProjectKey;
	const string SandboxApiKey = MemoryCanonApiFixture.SandboxApiKey;

	readonly MemoryCanonApiFixture _fx;
	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	public MemoryCanonApiTests(MemoryCanonApiFixture fx)
	{
		_fx = fx;
		_factory = fx.Factory;
		_client = fx.Client;
	}

	public ValueTask InitializeAsync() => new(_fx.ResetAsync());

	public ValueTask DisposeAsync() => ValueTask.CompletedTask; // the fixture owns host teardown

	// Seed a canon entry of a scope through the service door (auto-vivifies the store).
	// The workspace canon lives in the project's workspace container under key `index` —
	// the same store/key as the project canon; the scope is the container, not a key suffix.
	// For the fixture's workspace "test" that container is "$ws-test".
	const string TestWorkspaceContainer = "$ws-test";

	async Task WriteCanonAsync(string projectKey, string body, string key = "index")
	{
		using var scope = _factory.Services.CreateScope();
		using var db = scope.ServiceProvider.GetRequiredService<ICoreDbFactory>().Open();
		// MemoryStore.CreateAsync requires a Projects row; lazy-ensure workspace containers.
		if (projectKey == TestWorkspaceContainer)
			await WorkspaceMemory.EnsureContainerAsync(db, "test");
		else if (projectKey == "$workspace")
			await WorkspaceMemory.EnsureContainerAsync(db, "$system");
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		await memory.UpsertAsync(projectKey, "canon",
			new[] { new MemoryEntryInput { Key = key, Version = 0, Type = "Reference", Description = "canon", Body = body } },
			[]);
	}

	[Fact]
	public async Task Canon_BothScopesPresent_ReturnsBothParts()
	{
		await WriteCanonAsync(TestProjectKey, "PROJECT canon index");
		await WriteCanonAsync(TestWorkspaceContainer, "WORKSPACE canon index");

		_client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
		var resp = await _client.GetAsync($"/api/memory/{TestProjectKey}/canon");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await resp.Content.ReadFromJsonAsync<CanonResponse>();
		body.Should().NotBeNull();
		body!.Project.Should().NotBeNull();
		body.Project!.Body.Should().Be("PROJECT canon index");
		body.Project.Version.Should().BeGreaterThan(0);
		body.Workspace.Should().NotBeNull();
		body.Workspace!.Body.Should().Be("WORKSPACE canon index");
	}

	// ── SANDBOX CONTAINMENT ON THE DERIVED WORKSPACE LEG ─────────────────────────────────────────
	//
	// THE LEAK THIS PINS, measured on production 2026-07-26 with the real smoke key:
	//   GET /api/memory/smoke/canon    -> 200, project:null, workspace: 1309 bytes of owner facts
	//   GET /api/memory/$system/canon  -> 403
	//   GET /api/memory/kpvotes/canon  -> 403
	// The controls are what make it decisive: the gate refused that key on every target it was aimed
	// at, and the shared container came back anyway — as the ENTIRE body, the sandbox project's own
	// canon being empty. The route is not aimable at another tenant, which is exactly why it was
	// believed safe; "not aimable" is a statement about an attacker, not about what the caller is
	// entitled to receive.
	//
	// The project leg is untouched: the key owns that project and the wiring hook still gets it.
	[Fact]
	public async Task Canon_SandboxOnlyKey_GetsItsProjectCanon_ButNotTheWorkspaceCanon()
	{
		await WriteCanonAsync(SandboxProjectKey, "SANDBOX project canon");
		await WriteCanonAsync(TestWorkspaceContainer, "WORKSPACE canon index");

		_client.DefaultRequestHeaders.Add("X-Api-Key", SandboxApiKey);
		var resp = await _client.GetAsync($"/api/memory/{SandboxProjectKey}/canon");
		resp.StatusCode.Should().Be(HttpStatusCode.OK,
			"the call is not forbidden — the key owns this sandbox project and is entitled to its canon; "
			+ "only the derived workspace leg is withheld");

		var body = await resp.Content.ReadFromJsonAsync<CanonResponse>();
		body.Should().NotBeNull();
		body!.Project.Should().NotBeNull("the wiring hook must keep working for a sandboxOnly key");
		body.Project!.Body.Should().Be("SANDBOX project canon");

		body.Workspace.Should().BeNull(
			"a sandboxOnly key cannot reach the workspace's shared memory container: the container is not "
			+ "a sandbox row, it is fed by every project in the workspace including non-sandbox ones, and "
			+ "the PEP never judged it because the caller named the PROJECT. A null workspace part is "
			+ "already a valid 200 shape, so nothing about the response contract changes");
	}

	// The differential. If the leg were suppressed for EVERYONE the assertion above would still pass
	// while the canon injection had been broken for every ordinary key in the installation — a far
	// bigger outage than the leak. Same route, same workspace, same seeded canon; only the key differs.
	[Fact]
	public async Task Canon_OrdinaryKey_StillGetsTheWorkspaceCanon()
	{
		await WriteCanonAsync(TestProjectKey, "PROJECT canon index");
		await WriteCanonAsync(TestWorkspaceContainer, "WORKSPACE canon index");

		_client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
		var resp = await _client.GetAsync($"/api/memory/{TestProjectKey}/canon");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await resp.Content.ReadFromJsonAsync<CanonResponse>();
		body!.Workspace.Should().NotBeNull(
			"containment applies to sandboxOnly keys ONLY — an ordinary project key still receives its "
			+ "workspace's shared canon, which is the whole point of the endpoint returning two legs");
		body.Workspace!.Body.Should().Be("WORKSPACE canon index");
	}

	// Isolation: a project in workspace "test" must NOT surface the global "$workspace"
	// ($system) canon — only its own "$ws-test" container.
	[Fact]
	public async Task Canon_UsesProjectWorkspaceContainer_NotGlobalWorkspace()
	{
		await WriteCanonAsync("$workspace", "GLOBAL $workspace canon — must not leak");
		await WriteCanonAsync(TestWorkspaceContainer, "per-workspace canon for test");

		_client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
		var resp = await _client.GetAsync($"/api/memory/{TestProjectKey}/canon");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await resp.Content.ReadFromJsonAsync<CanonResponse>();
		body.Should().NotBeNull();
		body!.Workspace.Should().NotBeNull();
		body.Workspace!.Body.Should().Be("per-workspace canon for test");
		body.Workspace.Body.Should().NotContain("GLOBAL");
	}

	// canon-invisible-and-unfed: an empty scope used to answer with a bare null part — 200,
	// carrying nothing, no hint that a `canon` store even exists. Both legs are QUERIED here
	// (an ordinary key, workspace container reachable) and both come back at Version 0 instead
	// of vanishing silently — that discriminator is what the store/key/budget knowledge used
	// to live ONLY in MemoryService.cs's comments now rides on.
	//
	// canon-banner-empty-notice-unlabelled: Body is "" here, not a curation-nudge string — the
	// nudge PROSE is the kit's job (canon.ts), attributed to the specific leg under its own
	// heading; this endpoint only signals emptiness (Version 0), never renders it as text.
	[Fact]
	public async Task Canon_NoEntries_ReturnsEmptyBodyVersion0OnBothLegs_Still200()
	{
		_client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
		var resp = await _client.GetAsync($"/api/memory/{TestProjectKey}/canon");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await resp.Content.ReadFromJsonAsync<CanonResponse>();
		body.Should().NotBeNull();
		body!.Project.Should().NotBeNull("a queried scope is never a bare null — empty gets Version 0, not silence");
		body.Project!.Body.Should().Be("");
		body.Project.Version.Should().Be(0);
		body.Workspace.Should().NotBeNull("the workspace container IS reachable for this ordinary key — it is just empty");
		body.Workspace!.Body.Should().Be("");
		body.Workspace.Version.Should().Be(0);
	}

	[Fact]
	public async Task Canon_NoApiKey_Returns401()
	{
		var resp = await _client.GetAsync($"/api/memory/{TestProjectKey}/canon");
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}
}
