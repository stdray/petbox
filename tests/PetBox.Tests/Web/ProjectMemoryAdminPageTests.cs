using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Memory.Services;
using PetBox.Web.Auth;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Web;

// The admin memory page (spec: memoverhaul store taxonomy / ui-admin-memory-system-store-guard):
// system stores (IsSystem — e.g. session-digests) are machine plumbing and must be protected from
// deletion the way the petbox self-log is on ProjectLogs. The disabled Delete button is not
// security, so the POST handler REJECTS a system-store delete server-side (defense in depth).
public sealed class ProjectMemoryAdminPageTests : IDisposable
{
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;
	readonly ScopedDbFactory<MemoryDb> _factory;
	readonly MemoryStore _store;
	readonly MemoryService _memory;

	public ProjectMemoryAdminPageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-memadmin-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Project { Key = Proj, WorkspaceKey = "ws", Name = "P", Description = "" });
		_factory = new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
			c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure);
		_store = new MemoryStore(_db.Factory(), _factory);
		_memory = new MemoryService(_store);
	}

	public void Dispose()
	{
		_db.Dispose();
		_factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
		TestDirs.CleanupOrDefer(_dir);
	}

	static FeatureFlags Features()
	{
		var cfg = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Memory"] = "true" })
			.Build();
		return new FeatureFlags(cfg);
	}

	ProjectMemoryModel Page() =>
		new(new ProjectDirectory(_db.Factory()), Features(), _memory) { WorkspaceKey = "ws", ProjectKey = Proj };

	[Fact]
	public async Task Delete_OrdinaryStore_Removes()
	{
		await _store.CreateAsync(Proj, "notes", null);

		var result = await Page().OnPostDeleteAsync("notes");

		result.Should().BeOfType<RedirectToPageResult>();
		(await _store.ExistsAsync(Proj, "notes")).Should().BeFalse();
	}

	[Fact]
	public async Task Delete_SystemStore_Rejected_StorePreserved()
	{
		// session-digests is a well-known system store (MemoryStore.SystemStoreNames) — tagged
		// IsSystem on creation, so the guard fires.
		await _store.CreateAsync(Proj, "session-digests", null);

		var page = Page();
		var result = await page.OnPostDeleteAsync("session-digests");

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().Contain("cannot be deleted");
		(await _store.ExistsAsync(Proj, "session-digests")).Should().BeTrue();
	}

	[Fact]
	public async Task OnGet_MarksSystemStores_IsSystem()
	{
		await _store.CreateAsync(Proj, "session-digests", null);
		await _store.CreateAsync(Proj, "notes", null);

		var page = Page();
		await page.OnGetAsync();

		page.Stores.Single(s => s.Name == "session-digests").IsSystem.Should().BeTrue();
		page.Stores.Single(s => s.Name == "notes").IsSystem.Should().BeFalse();
	}
}
