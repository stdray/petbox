using System.Collections.Concurrent;
using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Registry;
using PetBox.Web.Settings;

namespace PetBox.Tests.Web;

// RED REPRO — the SECOND head of the prod bug fixed in 95a8e4b. That commit gave TaskBoardStore's
// core-db reads their own connection, but the cross-scope search fan-out
// (CrossScopeTaskSearchService, up to MaxProjectConcurrency=6 branches inside ONE request scope)
// still reaches the SCOPED PetBoxDb — a LinqToDB DataConnection, not thread-safe — down a second
// path, the LLM leg:
//
//   SearchOneProjectAsync -> TasksService.SearchNodesAsync -> VectorSearchIndex
//     -> LlmClientEmbedder -> CapabilityRouter                     (AddScoped)
//     -> LlmRegistryLevelResolver (AddScoped, holds the scoped PetBoxDb)
//          db.Projects / db.LlmRoutes / db.LlmEndpoints
//          -> SettingsResolver (AddScoped, same PetBoxDb) -> db.Settings, twice per resolve
//
// Same faces as before: "Must add values for the following parameters: @p, @scopeKey, @Key",
// "Collection was modified", ObjectDisposedException. It does NOT 500 today only because the
// search facade swallows a query-time embed failure (degraded=true) and the fan-out swallows a
// dead branch — so the user-visible symptom is a silently lopsided search, not an error page.
public sealed class LlmRegistryResolverRacePrimitiveTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ISecretEncryptor _secrets;

	public LlmRegistryResolverRacePrimitiveTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-llmreg-race-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		for (var i = 0; i < 8; i++)
			_db.Insert(new Project { Key = $"proj-{i}", WorkspaceKey = "ws1", Name = $"P{i}", Description = "" });
		_secrets = new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions()));
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// LlmRegistryLevelResolver.ResolveAsync — the per-branch registry read.
	[Fact]
	public async Task ConcurrentResolve_OnOneScopedPetBoxDb_MustNotThrow()
	{
		var resolver = new LlmRegistryLevelResolver(
			_db.Factory(), _secrets, new SettingsResolver(_db.Factory(), _secrets), NullLogger<LlmRegistryLevelResolver>.Instance);

		var seen = new ConcurrentBag<string>();
		for (var attempt = 0; attempt < 50 && seen.IsEmpty; attempt++)
			await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
			{
				try { await resolver.ResolveAsync($"proj-{i}"); }
				catch (Exception ex) { seen.Add(ex.ToString()); }
			})));

		seen.Distinct().Should().BeEmpty();
	}

	// SettingsResolver.GetAsync — reached twice per resolve (InheritAllowedAsync), and by every
	// other scoped reader of settings that a fanned-out request touches.
	[Fact]
	public async Task ConcurrentSettingsRead_OnOneScopedPetBoxDb_MustNotThrow()
	{
		var settings = new SettingsResolver(_db.Factory(), _secrets);
		var seen = new ConcurrentBag<string>();
		for (var attempt = 0; attempt < 50 && seen.IsEmpty; attempt++)
			await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
			{
				try { await settings.GetAsync<LlmRegistryInheritanceSettings>(Scope.Workspace, "ws1"); }
				catch (Exception ex) { seen.Add(ex.ToString()); }
			})));

		seen.Distinct().Should().BeEmpty();
	}
}

// The same race on the REAL host and the REAL DI graph: one request scope, its ONE scoped
// ILlmClient, driven from as many threads as the fan-out uses.
public sealed class LlmRegistryResolverRaceHostFixture : IAsyncLifetime
{
	public const int Projects = 8;

	public WebApplicationFactory<Program> Factory { get; }

	public LlmRegistryResolverRaceHostFixture()
	{
		Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
		Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment("Testing");
			b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["ConnectionStrings:PetBox"] = TestSchema.NewTempConnectionString("petbox-llmreg-race"),
				["Host:BackgroundServices"] = "false",
				["Features:LlmRouter"] = "true",
			}));
		});
	}

	public async Task InitializeAsync()
	{
		var cs = Factory.Services.GetRequiredService<IConfiguration>().GetConnectionString("PetBox")!;
		TestSchema.Core(cs);

		using var scope = Factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<PetBoxDb>();
		for (var i = 0; i < Projects; i++)
			await db.InsertAsync(new Project { Key = $"proj-{i}", WorkspaceKey = "$system", Name = $"P{i}", Description = "" });
	}

	public async Task DisposeAsync() => await Factory.DisposeAsync();
}

public sealed class LlmRegistryResolverRaceHostTests(LlmRegistryResolverRaceHostFixture fx)
	: IClassFixture<LlmRegistryResolverRaceHostFixture>
{
	// EmbedAsync is exactly what each fan-out branch ends up calling (VectorSearchIndex ->
	// LlmClientEmbedder). LlmRouterException ("no route configured") is the expected, benign answer
	// here — anything else is the shared-DataConnection race.
	[Fact]
	public async Task ScopedLlmClient_FromTheFanOutsThreads_DoesNotRaceTheScopedPetBoxDb()
	{
		var seen = new ConcurrentBag<string>();
		for (var attempt = 0; attempt < 20 && seen.IsEmpty; attempt++)
		{
			using var scope = fx.Factory.Services.CreateScope();
			var llm = scope.ServiceProvider.GetRequiredService<ILlmClient>();
			await Task.WhenAll(Enumerable.Range(0, LlmRegistryResolverRaceHostFixture.Projects).Select(i => Task.Run(async () =>
			{
				try { await llm.EmbedAsync($"proj-{i}", new EmbedRequest(["llm"])); }
				catch (LlmRouterException) { /* no route configured — the honest answer, not a race */ }
				catch (Exception ex) { seen.Add(ex.ToString()); }
			})));
		}

		seen.Distinct().Should().BeEmpty();
	}
}
