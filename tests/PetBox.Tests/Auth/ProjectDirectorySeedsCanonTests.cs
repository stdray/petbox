using LinqToDB;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Auth;
using PetBox.Web.Memory;

namespace PetBox.Tests.Auth;

// canon-invisible-and-unfed (item 2): ProjectDirectory.CreateAsync now seeds a ~10-line canon
// skeleton (store `canon`, key `index`) into every new project instead of leaving it totally
// silent until someone stumbles on MemoryService.cs's comments. The seed runs through a rented
// IServiceScopeFactory scope, resolving IProjectCanonSeeder (which itself wraps the Scoped
// IMemoryService) — ProjectDirectory is a Singleton, so capturing either directly in the
// constructor would be exactly the captive dependency CaptiveDependencyTests forbids. The actual
// memory-store call lives in ProjectCanonSeeder, deliberately NOT in ProjectDirectory.cs — see
// that class's header comment for why (SandboxContainmentCallSiteGuardTests).
//
// THE MAIN RISK the card calls out by name: a repeat seed must never duplicate or overwrite an
// already-curated canon. This is closed BY CONSTRUCTION (memory_upsert's Version 0 means
// "create — nothing to clobber"; TemporalStore.UpsertAsync rejects a second create against a
// DIFFERENT existing payload as Stale, and no-ops against an IDENTICAL one), not by a guard
// written in ProjectDirectory that could itself have a gap — SecondSeedNeverClobbersACuratedCanon
// pins the outcome, not the mechanism.
public sealed class ProjectDirectorySeedsCanonTests : IDisposable
{
	readonly string _dir;
	readonly ICoreDbFactory _dbf;
	readonly ServiceProvider _provider;
	readonly IMemoryService _memory;

	public ProjectDirectorySeedsCanonTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-seedcanon-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_dbf = new CoreDbFactory(cs);

		using (var seed = _dbf.Open())
			seed.Insert(new Workspace { Key = "alpha", Name = "alpha", Description = "", CreatedAt = DateTime.UtcNow });

		var memFactory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);

		var services = new ServiceCollection();
		services.AddSingleton(_dbf);
		services.AddSingleton<IScopedDbFactory<MemoryDb>>(memFactory);
		services.AddScoped<IMemoryStore, MemoryStore>();
		services.AddScoped<IMemoryService, MemoryService>();
		services.AddScoped<IProjectCanonSeeder, ProjectCanonSeeder>();
		_provider = services.BuildServiceProvider();

		// A standalone instance over the SAME files, for direct assertions/curation — the test
		// is single-threaded, so sharing it alongside the scopes ProjectDirectory rents is safe.
		_memory = _provider.GetRequiredService<IMemoryService>();
	}

	public void Dispose()
	{
		_provider.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	ProjectDirectory NewDirectory() =>
		new(_dbf, new MemoryCache(new MemoryCacheOptions()), ttl: null,
			scopes: _provider.GetRequiredService<IServiceScopeFactory>());

	[Fact]
	public async Task CreateAsync_SeedsTheCanonSkeleton()
	{
		var svc = NewDirectory();
		(await svc.CreateAsync("alpha", "app", "App", null)).Should().BeOfType<ProjectChangeResult.Created>();

		var entry = await _memory.GetAsync("app", "canon", "index");
		entry.Should().NotBeNull("a brand new project must not leave its canon totally silent");
		entry!.Body.Should().Contain("COMPACT INDEX OF POINTERS")
			.And.Contain("10,000 characters")
			.And.Contain("memory_upsert");
	}

	[Fact]
	public async Task SecondSeedNeverClobbersACuratedCanon()
	{
		var svc = NewDirectory();
		(await svc.CreateAsync("alpha", "app", "App", null)).Should().BeOfType<ProjectChangeResult.Created>();

		// The owner curates: a real edit at the version the skeleton landed on.
		var seeded = await _memory.GetAsync("app", "canon", "index");
		seeded.Should().NotBeNull();
		await _memory.UpsertAsync("app", "canon",
			[new MemoryEntryInput { Key = "index", Version = seeded!.Version, Type = "Reference", Body = "OWNER CURATED — do not touch" }],
			[]);

		// A repeat run of the SAME seed logic (a retried request, a re-provisioned host, a future
		// backfill reusing this method) must not win against the owner's edit.
		await svc.SeedCanonAsync("app", CancellationToken.None);

		var after = await _memory.GetAsync("app", "canon", "index");
		after!.Body.Should().Be("OWNER CURATED — do not touch",
			"memory_upsert Version 0 cannot clobber an existing DIFFERENT payload — TemporalStore " +
			"classifies it Stale and drops it, never applies it");
	}

	[Fact]
	public async Task SecondSeedAgainstAnUncuratedSkeleton_IsANoOp_NotADuplicate()
	{
		var svc = NewDirectory();
		(await svc.CreateAsync("alpha", "app", "App", null)).Should().BeOfType<ProjectChangeResult.Created>();
		var firstVersion = (await _memory.GetAsync("app", "canon", "index"))!.Version;

		// A repeat seed against an UNTOUCHED skeleton (identical payload) — the other half of
		// "idempotent": no second revision, no version bump, nothing to see in the delta.
		await svc.SeedCanonAsync("app", CancellationToken.None);

		var after = await _memory.GetAsync("app", "canon", "index");
		after!.Version.Should().Be(firstVersion, "an identical re-seed is a no-op, not a new revision");
	}

	[Fact]
	public async Task CreateAsync_StillSucceeds_WhenTheMemoryLayerIsUnavailable()
	{
		// No IMemoryService registered at all — stands in for a hand-wired host or a memory-layer
		// hiccup. SeedCanonAsync must swallow the failure: the project ROW is already committed by
		// the time it runs, and a curation nudge failing to seed is never a reason to refuse the
		// whole creation.
		var bareServices = new ServiceCollection();
		using var bareProvider = bareServices.BuildServiceProvider();
		var svc = new ProjectDirectory(_dbf, new MemoryCache(new MemoryCacheOptions()), ttl: null,
			scopes: bareProvider.GetRequiredService<IServiceScopeFactory>());

		var result = await svc.CreateAsync("alpha", "sad", "Sad", null);
		result.Should().BeOfType<ProjectChangeResult.Created>("a memory-layer failure must never block project creation");
	}

	[Fact]
	public async Task CreateAsync_WithNoScopeFactory_StillSucceeds()
	{
		// The plain `new ProjectDirectory(dbf)` shape every pre-existing test/call site uses:
		// scopes defaults to null, and the seed must silently skip rather than throw — an
		// existing (unwired) caller of CreateAsync must keep working exactly as before.
		var svc = new ProjectDirectory(_dbf);
		var result = await svc.CreateAsync("alpha", "plain", "Plain", null);
		result.Should().BeOfType<ProjectChangeResult.Created>();
	}
}
