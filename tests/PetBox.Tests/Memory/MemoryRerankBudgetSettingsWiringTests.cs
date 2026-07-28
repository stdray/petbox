using LinqToDB;
using Microsoft.Extensions.Options;
using PetBox.Config;
using PetBox.Core.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Search;
using PetBox.Core.Settings;
using PetBox.Memory.Contract;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Settings;

namespace PetBox.Tests.Memory;

// rerank-budget-params-to-settings, the production-wiring half: the settings mechanism resolving
// RerankBudgetSettings correctly (SettingsResolverTests) is necessary but NOT sufficient — the bug
// this card exists to fix is that NOTHING in production ever called FromSettings, so a project-level
// override could never reach a real search. This test goes through the REAL production door
// (MemoryService.SearchEntriesAsync, constructed exactly as DI constructs it, with a REAL
// SettingsResolver backed by real SQLite) rather than a hand-built `new SearchService(..., budget)` —
// the thing the coordinator's review flagged as still missing.
public sealed class MemoryRerankBudgetSettingsWiringTests : IDisposable
{
	const string Proj = "proj-budget-wire";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly ISettingsResolver _settings;
	readonly MemoryService _memory;

	public MemoryRerankBudgetSettingsWiringTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-membudgetwire-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws-budget-wire", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), TestSchema.Memory);
		_store = new MemoryStore(_db.Factory(), _factory);

		// The REAL settings resolver (PetBox.Web.Settings.SettingsResolver), the same class DI hands
		// to MemoryService's constructor in Program.cs — not a fake, not a hand-rolled stub.
		var secrets = new AesGcmSecretEncryptor(Options.Create(new SecretEncryptorOptions { MasterKey = "test-master-key" }));
		_settings = new SettingsResolver(new SettingsStore(_db.Factory()), secrets);

		// Constructed with the settings resolver in the SAME optional-collaborator slot DI fills —
		// this is the exact call shape `AddScoped<IMemoryService, MemoryService>()` produces.
		_memory = new MemoryService(_store, llm: null, rerank: null, log: null, poolCache: null, settings: _settings);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static MemoryEntryInput Entry(string key) =>
		new() { Key = key, Version = 0, Type = "Project", Description = "alpha note", Body = "the alpha keyword appears here" };

	[Fact]
	public async Task ProjectScopeOverride_ChangesTheRealSearchsPoolLimit_NotJustARecordInATest()
	{
		await _memory.CreateStoreAsync(Proj, "notes", null);
		// 5 lexically-matching entries — comfortably under the compiled-in default budget (160), so
		// the FIRST call proves the default reaches production untouched; the SECOND call proves an
		// override does too.
		await _memory.UpsertAsync(Proj, "notes",
			[Entry("a"), Entry("b"), Entry("c"), Entry("d"), Entry("e")], []);

		var before = await _memory.SearchEntriesAsync(Proj,
			new SearchRequest<MemoryEntryFilter, MemorySortBy> { Query = "alpha", WholePool = true });

		before.PoolLimit.Should().Be(new RerankCandidateBudget().Candidates()); // 160, the compiled-in default, reached with NO override
		before.PoolBounded.Should().BeFalse("5 matches is far under the budget");
		before.Hits.Should().HaveCount(5);

		// A Project-scope override (settings-uniform-override: deeper wins) that pins the budget to
		// exactly 2 candidates — now just the declared number itself
		// (rerank-budget-collapse-to-one-number), no back-solved latency bar required.
		await _settings.SetAsync(Scope.Project, Proj,
			new RerankBudgetSettings { Candidates = 2 }, new RerankBudgetSettings(), updatedBy: null);

		var after = await _memory.SearchEntriesAsync(Proj,
			new SearchRequest<MemoryEntryFilter, MemorySortBy> { Query = "alpha", WholePool = true });

		// THE proof: the REAL SearchEntriesAsync call — not a hand-built SearchService — now caps at
		// the overridden budget. Nothing in this test ever constructs a RerankCandidateBudget or a
		// SearchService directly; the override reached production through MemoryService's own
		// constructor-injected ISettingsResolver, exactly as it will in the running app.
		after.PoolLimit.Should().Be(2);
		after.PoolBounded.Should().BeTrue("5 matches now exceed the overridden 2-candidate budget");
		after.Hits.Should().HaveCount(2);
	}
}
