using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Memory.Data;
using PetBox.Tasks.Data;
using PetBox.Web.Auth;
using PetBox.Web.Pages.Admin;
using PetBox.Web.Search;

namespace PetBox.Tests.Web;

// ui-workspace-delete-cascade: a project can now be deleted from the admin project page, and
// the delete cascades every Core-DB row the project owns (keys, health, data/log/board/memory
// metadata, relations, settings). Reserved built-ins ($system / $workspace) refuse deletion.
public sealed class ProjectDeletePageTests : IDisposable
{
	const string Proj = "proj";
	const string Other = "other";

	readonly string _dir;
	readonly PetBoxDb _db;

	public ProjectDeletePageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-projdel-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	static FeatureFlags Features()
	{
		var cfg = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Data"] = "true" })
			.Build();
		return new FeatureFlags(cfg);
	}

	// The page holds no db factory any more (db-out-of-pages-into-services): it is built from the
	// SERVICES it asks, and these are the production implementations, not stubs. Log retention
	// (formerly needing ISettingsStore for the override lookup) moved off this page onto the generic
	// Settings page (admin-routes-and-pages item 3), so the constructor is one dependency shorter.
	// SearchReindexService (reindex-as-first-class-mechanism) is likewise wired for real — these
	// tests never click the reindex button, so llm:null (never dereferenced) is enough.
	ProjectDetailModel Page(string projectKey) =>
		new(
			new ProjectDirectory(_db.Factory()),
			_db.Factory().AgentKeys(),
			_db.Factory().HealthEndpoints(),
			Features(),
			new NullSettingsResolver(),
			new SearchReindexService(
				new ScopedDbFactory<MemoryDb>(Path.Combine(_dir, "memory"), Scope.Project,
					c => new MemoryDb(MemoryDb.CreateOptions(c)), MemorySchema.Ensure),
				new ScopedDbFactory<TasksDb>(Path.Combine(_dir, "tasks"), Scope.Project,
					c => new TasksDb(TasksDb.CreateOptions(c)), TasksSchema.Ensure),
				new ProjectCatalog(_db.Factory())))
		{ WorkspaceKey = "ws", ProjectKey = projectKey };

	// Seed one owned row in every table the cascade touches, for the given project key.
	void SeedOwnedResources(string projectKey)
	{
		var now = DateTime.UtcNow;
		_db.Insert(new Project { Key = projectKey, WorkspaceKey = "ws", Name = projectKey, Description = "" });
		_db.Insert(new ApiKey { Key = $"yb_key_{projectKey}", ProjectKey = projectKey, Scopes = "logs:query", Name = "k", CreatedAt = now });
		_db.Insert(new HealthEndpoint { ProjectKey = projectKey, Url = "https://x/health", Enabled = true, IntervalSeconds = 60, CreatedAt = now });
		_db.Insert(new DataDb { ProjectKey = projectKey, Name = "db1", MaxPageCount = 2048, CreatedAt = now, UpdatedAt = now });
		_db.Insert(new DataTable { Name = $"{projectKey}_t", ProjectKey = projectKey, Columns = "[]" });
		_db.Insert(new SavedQuery { Name = "q", Kql = "events", ProjectKey = projectKey, CreatedAt = now, UpdatedAt = now });
		_db.Insert(new ShareLink { Id = $"s_{projectKey}", ProjectKey = projectKey, LogName = "default", Kql = "events", CreatedAt = now, ExpiresAt = now.AddDays(1), SaltBase64 = "AA==", ColumnsJson = "[]", ModesJson = "{}", CreatedBy = "t" });
		_db.Insert(new LogMeta { ProjectKey = projectKey, Name = "app", CreatedAt = now, UpdatedAt = now });
		_db.Insert(new TaskBoardMeta { ProjectKey = projectKey, Name = "work", Kind = "simple", CreatedAt = now, UpdatedAt = now });
		_db.Insert(new MemoryStoreMeta { ProjectKey = projectKey, Name = "notes", CreatedAt = now, UpdatedAt = now, IsSystem = false });
		_db.Insert(new LegacyRelation { Id = $"r_{projectKey}", ProjectKey = projectKey, Kind = "blocks", FromNodeId = "a", ToNodeId = "b", CreatedAt = now });
		_db.Insert(new Setting { Scope = nameof(Scope.Project), ScopeKey = projectKey, Path = "log.retention.days", Type = "int", Value = "7", UpdatedAt = now });
	}

	int OwnedRowCount(string projectKey) =>
		_db.ApiKeys.Count(k => k.ProjectKey == projectKey)
		+ _db.HealthEndpoints.Count(e => e.ProjectKey == projectKey)
		+ _db.DataDbs.Count(d => d.ProjectKey == projectKey)
		+ _db.DataTables.Count(t => t.ProjectKey == projectKey)
		+ _db.SavedQueries.Count(q => q.ProjectKey == projectKey)
		+ _db.ShareLinks.Count(s => s.ProjectKey == projectKey)
		+ _db.Logs.Count(l => l.ProjectKey == projectKey)
		+ _db.TaskBoards.Count(b => b.ProjectKey == projectKey)
		+ _db.MemoryStores.Count(m => m.ProjectKey == projectKey)
		+ _db.LegacyRelations.Count(r => r.ProjectKey == projectKey)
		+ _db.Settings.Count(s => s.Scope == nameof(Scope.Project) && s.ScopeKey == projectKey);

	[Fact]
	public async Task Delete_project_cascades_all_owned_rows_and_leaves_other_projects_intact()
	{
		SeedOwnedResources(Proj);
		SeedOwnedResources(Other);
		OwnedRowCount(Proj).Should().Be(11);

		var page = Page(Proj);
		var result = await page.OnPostDeleteAsync();

		result.Should().BeOfType<RedirectResult>();
		_db.Projects.Any(p => p.Key == Proj).Should().BeFalse("the project row is gone");
		OwnedRowCount(Proj).Should().Be(0, "every owned bookkeeping row is cascaded");

		// The sibling project and its rows are untouched — the cascade is project-scoped.
		_db.Projects.Any(p => p.Key == Other).Should().BeTrue();
		OwnedRowCount(Other).Should().Be(11);
	}

	[Theory]
	[InlineData("$system")]
	[InlineData("$workspace")]
	public async Task Delete_reserved_project_is_refused(string reserved)
	{
		// $system is seeded by M001; $workspace by M028 — both already exist.
		var before = _db.Projects.Count(p => p.Key == reserved);
		before.Should().Be(1, "reserved project is seeded by migrations");

		var page = Page(reserved);
		var result = await page.OnPostDeleteAsync();

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().Contain(reserved);
		_db.Projects.Any(p => p.Key == reserved).Should().BeTrue("a reserved project is never deleted");
	}
}
