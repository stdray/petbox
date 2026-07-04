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
						["ConnectionStrings:PetBox"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"petbox-test-{Guid.NewGuid():N}.db")};Cache=Shared",
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
						cs => new MemoryDb(MemoryDb.CreateOptions(cs)), MemorySchema.Ensure));
				});
			});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs); // runs migrations: seeds $system + $workspace projects
		Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		await db.ApiKeys.Where(k => k.Key == TestApiKey).DeleteAsync();
		await db.Projects.Where(p => p.Key == TestProjectKey).DeleteAsync();
		await db.Workspaces.Where(w => w.Key == "test").DeleteAsync();

		await db.InsertAsync(new Workspace { Key = "test", Name = "Test", CreatedAt = DateTime.UtcNow });
		await db.InsertAsync(new Project { Key = TestProjectKey, WorkspaceKey = "test", Name = "KpVotes" });
		await db.InsertAsync(new ApiKey { Key = TestApiKey, ProjectKey = TestProjectKey, Scopes = "memory:read,memory:write", CreatedAt = DateTime.UtcNow });
	}

	// Per-test reset under the shared host: strip the auth header a previous test added to
	// the shared client, and delete the per-scope memory store files a previous test seeded
	// (pool handles released first).
	public async Task ResetAsync()
	{
		Client.DefaultRequestHeaders.Remove("X-Api-Key");

		// The factory caches per (scope, store) — evict the canon store of both scopes so
		// the cached contexts release their file handles before the deletes below.
		var memFactory = Factory.Services.GetRequiredService<IScopedDbFactory<MemoryDb>>();
		await memFactory.EvictAsync(TestProjectKey, "canon");
		await memFactory.EvictAsync("$workspace", "canon");
		if (!Directory.Exists(_baseDir)) return;
		TestDirs.ClearPoolsUnder(_baseDir);
		foreach (var file in Directory.EnumerateFiles(_baseDir, "*.db", SearchOption.AllDirectories))
			if (!PetBox.Core.Data.ScopedDbFiles.TryDelete(file))
				throw new InvalidOperationException($"per-test reset could not delete {file} (still locked)");
	}

	public async Task DisposeAsync()
	{
		Client.Dispose();
		await Factory.DisposeAsync();
		TestDirs.CleanupOrDefer(_baseDir);
	}
}

// GET /api/memory/{projectKey}/canon (spec agent-wiring, memory-canon-storage): the wiring-hook
// read surface for the curated memory canon. Returns the project's canon index and the shared
// workspace canon index; missing parts are null (still 200); no key is 401.
public sealed class MemoryCanonApiTests : IClassFixture<MemoryCanonApiFixture>, IAsyncLifetime
{
	const string TestProjectKey = MemoryCanonApiFixture.TestProjectKey;
	const string TestApiKey = MemoryCanonApiFixture.TestApiKey;

	readonly MemoryCanonApiFixture _fx;
	readonly WebApplicationFactory<Program> _factory;
	readonly HttpClient _client;

	public MemoryCanonApiTests(MemoryCanonApiFixture fx)
	{
		_fx = fx;
		_factory = fx.Factory;
		_client = fx.Client;
	}

	public Task InitializeAsync() => _fx.ResetAsync();

	public Task DisposeAsync() => Task.CompletedTask; // the fixture owns host teardown

	// Seed a canon entry of a scope through the service door (auto-vivifies the store).
	// The workspace canon lives in the reserved `$workspace` container under key `index` —
	// the same store/key as the project canon; the scope is the container, not a key suffix.
	async Task WriteCanonAsync(string projectKey, string body, string key = "index")
	{
		using var scope = _factory.Services.CreateScope();
		var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
		await memory.UpsertAsync(projectKey, "canon",
			new[] { new MemoryEntryInput { Key = key, Version = 0, Type = "Reference", Description = "canon", Body = body } },
			[]);
	}

	[Fact]
	public async Task Canon_BothScopesPresent_ReturnsBothParts()
	{
		await WriteCanonAsync(TestProjectKey, "PROJECT canon index");
		await WriteCanonAsync("$workspace", "WORKSPACE canon index");

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

	[Fact]
	public async Task Canon_NoEntries_ReturnsNullParts_Still200()
	{
		_client.DefaultRequestHeaders.Add("X-Api-Key", TestApiKey);
		var resp = await _client.GetAsync($"/api/memory/{TestProjectKey}/canon");
		resp.StatusCode.Should().Be(HttpStatusCode.OK);

		var body = await resp.Content.ReadFromJsonAsync<CanonResponse>();
		body.Should().NotBeNull();
		body!.Project.Should().BeNull();
		body.Workspace.Should().BeNull();
	}

	[Fact]
	public async Task Canon_NoApiKey_Returns401()
	{
		var resp = await _client.GetAsync($"/api/memory/{TestProjectKey}/canon");
		resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
	}
}
