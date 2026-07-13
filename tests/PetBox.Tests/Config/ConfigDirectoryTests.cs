using LinqToDB;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Tests.Config;

// IConfigDirectory is the service layer this wave gave PetBox.Config (db-out-of-pages-remaining-24,
// group B item 3): the one door Pages.Config.IndexModel and ConfigApi both go through instead of
// holding IConfigDbFactory/ICoreDbFactory. Exercises the exact persistence sequences lifted out of
// those two callers — Delete's history-then-soft-delete, Reveal's history row, Create's
// ContentHash, the REST delete's deliberate NO history, and the SavedConfigFilters upsert — plus
// the project->workspace lookup and the ResolvePipeline pass-through.
public sealed class ConfigDirectoryTests : IDisposable
{
	const string Ws = "cfgdir-ws";
	readonly string _dir;
	readonly ScopedDbFactory<ConfigDb> _scopedConfig;
	readonly ConfigDbFactory _configFactory;
	readonly ICoreDbFactory _coreFactory;
	readonly ConfigDirectory _svc;

	public ConfigDirectoryTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-configdir-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(_dir);

		_scopedConfig = new ScopedDbFactory<ConfigDb>(Path.Combine(_dir, "config"), Scope.Workspace,
			c => new ConfigDb(ConfigDb.CreateOptions(c)), ConfigSchema.Ensure);
		_configFactory = new ConfigDbFactory(_scopedConfig);

		var coreCs = TestSchema.NewTempConnectionString("petbox-configdir-core");
		TestSchema.Core(coreCs);
		_coreFactory = TestCoreDb.CoreFactory(coreCs);

		_svc = new ConfigDirectory(_configFactory, _coreFactory);
	}

	public void Dispose()
	{
		_scopedConfig.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	ConfigDb OpenConfigDb() => _configFactory.NewConfigDb(Ws);

	// --- Bindings ---

	[Fact]
	public async Task ListActiveBindings_ExcludesDeleted_OrdersByPath()
	{
		using (var db = OpenConfigDb())
		{
			await db.InsertAsync(NewBinding("b.path", "ws:" + Ws));
			await db.InsertAsync(NewBinding("a.path", "ws:" + Ws));
			await db.InsertAsync(NewBinding("z.deleted", "ws:" + Ws) with { IsDeleted = true });
		}

		var rows = await _svc.ListActiveBindingsAsync(Ws);

		rows.Select(r => r.Path).Should().Equal("a.path", "b.path");
	}

	[Fact]
	public async Task GetBinding_ReturnsRow_EvenWhenDeleted()
	{
		long id;
		using (var db = OpenConfigDb())
			id = Convert.ToInt64(await db.InsertWithIdentityAsync(NewBinding("x", "ws:" + Ws) with { IsDeleted = true }));

		var found = await _svc.GetBindingAsync(Ws, id);

		found.Should().NotBeNull("Reveal's lookup never filtered IsDeleted, and this mirrors it exactly");
		found!.Path.Should().Be("x");
	}

	[Fact]
	public async Task GetBinding_UnknownId_ReturnsNull()
	{
		(await _svc.GetBindingAsync(Ws, 999_999)).Should().BeNull();
	}

	[Fact]
	public async Task CreateBinding_Inserts_WithComputedContentHash()
	{
		var created = await _svc.CreateBindingAsync(Ws, "app/x", "v1", $"ws:{Ws}", BindingKind.Plain);

		created.Id.Should().BeGreaterThan(0);
		created.ContentHash.Should().Be(BindingContentHash.Compute("app/x", $"ws:{Ws}", BindingKind.Plain, "v1", null));

		using var db = OpenConfigDb();
		db.Bindings.Count(b => b.Id == created.Id && b.Path == "app/x" && b.Value == "v1").Should().Be(1);
	}

	[Fact]
	public async Task DeleteBindingById_WritesDeleteHistory_ThenSoftDeletes()
	{
		long id;
		using (var db = OpenConfigDb())
			id = Convert.ToInt64(await db.InsertWithIdentityAsync(NewBinding("secret.one", "ws:" + Ws) with { Kind = BindingKind.Secret, Value = "plain-leftover" }));

		var deleted = await _svc.DeleteBindingByIdAsync(Ws, id, "alice");

		deleted.Should().BeTrue();
		using var db2 = OpenConfigDb();
		var row = db2.Bindings.Single(b => b.Id == id);
		row.IsDeleted.Should().BeTrue();
		row.DeletedAt.Should().NotBeNull();

		var hist = db2.History.Single(h => h.BindingId == id);
		hist.Action.Should().Be("Delete");
		hist.Actor.Should().Be("alice");
		hist.OldValue.Should().Be("(secret)", "a Secret binding's value is redacted in the history row, never leaked");
	}

	[Fact]
	public async Task DeleteBindingById_PlainKind_RecordsOldValueVerbatim()
	{
		long id;
		using (var db = OpenConfigDb())
			id = Convert.ToInt64(await db.InsertWithIdentityAsync(NewBinding("plain.one", "ws:" + Ws) with { Value = "42" }));

		await _svc.DeleteBindingByIdAsync(Ws, id, "bob");

		using var db2 = OpenConfigDb();
		db2.History.Single(h => h.BindingId == id).OldValue.Should().Be("42");
	}

	[Fact]
	public async Task DeleteBindingById_MissingOrAlreadyDeleted_IsNoOp_NoHistoryWritten()
	{
		(await _svc.DeleteBindingByIdAsync(Ws, 12345, "alice")).Should().BeFalse();

		long id;
		using (var db = OpenConfigDb())
			id = Convert.ToInt64(await db.InsertWithIdentityAsync(NewBinding("already-gone", "ws:" + Ws) with { IsDeleted = true }));

		(await _svc.DeleteBindingByIdAsync(Ws, id, "alice")).Should().BeFalse();

		using var db2 = OpenConfigDb();
		db2.History.Count(h => h.BindingId == id).Should().Be(0, "a no-op delete must not fabricate a history row");
	}

	[Fact]
	public async Task DeleteBindingByPathTags_SoftDeletes_ButWritesNoHistory()
	{
		using (var db = OpenConfigDb())
			await db.InsertAsync(NewBinding("rest.deleted", $"ws:{Ws},env:dev"));

		var deleted = await _svc.DeleteBindingByPathTagsAsync(Ws, "rest.deleted", $"ws:{Ws},env:dev");

		deleted.Should().BeTrue();
		using var db2 = OpenConfigDb();
		db2.Bindings.Single(b => b.Path == "rest.deleted").IsDeleted.Should().BeTrue();
		db2.History.Count().Should().Be(0,
			"the REST delete never wrote history before this refactor, and that asymmetry with the page's delete is preserved on purpose");
	}

	[Fact]
	public async Task DeleteBindingByPathTags_NoMatch_ReturnsFalse()
	{
		(await _svc.DeleteBindingByPathTagsAsync(Ws, "nope", "ws:" + Ws)).Should().BeFalse();
	}

	[Fact]
	public async Task RecordReveal_WritesRevealHistoryRow_WithNoValues()
	{
		long id;
		using (var db = OpenConfigDb())
			id = Convert.ToInt64(await db.InsertWithIdentityAsync(NewBinding("secret.two", "ws:" + Ws) with { Kind = BindingKind.Secret }));

		var binding = await _svc.GetBindingAsync(Ws, id);
		await _svc.RecordRevealAsync(Ws, binding!, "carol");

		using var db2 = OpenConfigDb();
		var hist = db2.History.Single(h => h.BindingId == id);
		hist.Action.Should().Be("Reveal");
		hist.Actor.Should().Be("carol");
		hist.OldValue.Should().BeNull();
		hist.NewValue.Should().BeNull();
	}

	[Fact]
	public async Task ResolveAll_DelegatesToResolvePipeline_SameAmbiguityRule()
	{
		using (var db = OpenConfigDb())
		{
			// Both are subsets of the 3-tag request set below and equally specific (2 tags each) ->
			// ambiguous, the same rule ResolvePipelineTests documents directly.
			await db.InsertAsync(NewBinding("app/x", $"ws:{Ws},env:dev"));
			await db.InsertAsync(NewBinding("app/x", $"ws:{Ws},region:ru"));
		}

		var act = async () => await _svc.ResolveAllAsync(Ws, [$"ws:{Ws}", "env:dev", "region:ru"]);
		await act.Should().ThrowAsync<AmbiguousConfigException>();

		// Narrowing the request tags so only one candidate is a subset resolves cleanly.
		var matches = await _svc.ResolveAllAsync(Ws, [$"ws:{Ws}", "env:dev"]);
		matches.Should().ContainSingle().Which.Binding.Tags.Should().Contain("env:dev");
	}

	// --- Project -> workspace ---

	[Fact]
	public async Task GetProjectWorkspace_KnownProject_ReturnsItsWorkspace()
	{
		using (var db = _coreFactory.Open())
		{
			await db.InsertAsync(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
			await db.InsertAsync(new Project { Key = "proj-a", WorkspaceKey = Ws, Name = "A" });
		}

		(await _svc.GetProjectWorkspaceAsync("proj-a")).Should().Be(Ws);
	}

	[Fact]
	public async Task GetProjectWorkspace_UnknownProject_ReturnsNull()
	{
		(await _svc.GetProjectWorkspaceAsync("no-such-project")).Should().BeNull();
	}

	// --- Saved filters (core.db) ---

	[Fact]
	public async Task SaveFilter_Inserts_ThenUpdatesInPlace_ByWorkspaceAndName()
	{
		await _svc.SaveFilterAsync(Ws, "my-filter", "env=dev");
		(await _svc.ListSavedFiltersAsync(Ws)).Should().ContainSingle()
			.Which.FilterTags.Should().Be("env=dev");

		await _svc.SaveFilterAsync(Ws, "my-filter", "env=prod,region=ru");

		var filters = await _svc.ListSavedFiltersAsync(Ws);
		filters.Should().ContainSingle("a same-name save updates the existing row instead of duplicating it")
			.Which.FilterTags.Should().Be("env=prod,region=ru");
	}

	[Fact]
	public async Task ListSavedFilters_OrderedByName_ScopedToWorkspace()
	{
		await _svc.SaveFilterAsync(Ws, "zeta", "a=1");
		await _svc.SaveFilterAsync(Ws, "alpha", "a=1");
		await _svc.SaveFilterAsync("other-ws", "middle", "a=1");

		(await _svc.ListSavedFiltersAsync(Ws)).Select(f => f.Name).Should().Equal("alpha", "zeta");
	}

	[Fact]
	public async Task DeleteFilter_RemovesOnlyWithinItsWorkspace()
	{
		await _svc.SaveFilterAsync(Ws, "keep-me", "a=1");
		await _svc.SaveFilterAsync(Ws, "drop-me", "a=1");
		var toDrop = (await _svc.ListSavedFiltersAsync(Ws)).Single(f => f.Name == "drop-me");

		await _svc.DeleteFilterAsync(Ws, toDrop.Id);

		(await _svc.ListSavedFiltersAsync(Ws)).Select(f => f.Name).Should().Equal("keep-me");
	}

	static ConfigBinding NewBinding(string path, string tags) => new()
	{
		Path = path,
		Value = "v",
		Tags = tags,
		Kind = BindingKind.Plain,
		Version = 1,
		ContentHash = "h",
		CreatedAt = DateTime.UtcNow,
		UpdatedAt = DateTime.UtcNow,
	};
}
