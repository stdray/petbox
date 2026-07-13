using LinqToDB;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;

namespace PetBox.Tests.Data;

// ICoreDbRollupService is the COUNTS door onto core.db: Admin/Index, Dashboard/Index and
// ProjectHome/Index ask it instead of opening core.db themselves (db-out-of-pages-remaining-24).
public sealed class CoreDbRollupServiceTests
{
	static (CoreDbRollupService Svc, ICoreDbFactory Dbf) New()
	{
		var cs = TestSchema.NewTempConnectionString();
		TestSchema.Core(cs);
		var dbf = new CoreDbFactory(cs);
		return (new CoreDbRollupService(dbf), dbf);
	}

	static void SeedWorkspace(ICoreDbFactory dbf, string key)
	{
		using var db = dbf.Open();
		db.Insert(new Workspace { Key = key, Name = key, Description = "", CreatedAt = DateTime.UtcNow });
	}

	static void SeedProject(ICoreDbFactory dbf, string key, string ws)
	{
		using var db = dbf.Open();
		db.Insert(new Project { Key = key, WorkspaceKey = ws, Name = key, Description = "" });
	}

	static void SeedUser(ICoreDbFactory dbf, string username)
	{
		using var db = dbf.Open();
		db.Insert(new User { Username = username, PasswordHash = "x", CreatedAt = DateTime.UtcNow });
	}

	static void SeedApiKey(ICoreDbFactory dbf, string key, string projectKey)
	{
		using var db = dbf.Open();
		db.Insert(new ApiKey { Key = key, ProjectKey = projectKey, Name = key, Scopes = "data:read", CreatedAt = DateTime.UtcNow });
	}

	static void SeedDataDb(ICoreDbFactory dbf, string projectKey, string name)
	{
		using var db = dbf.Open();
		db.Insert(new DataDb { ProjectKey = projectKey, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
	}

	static void SeedLog(ICoreDbFactory dbf, string projectKey, string name)
	{
		using var db = dbf.Open();
		db.Insert(new LogMeta { ProjectKey = projectKey, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
	}

	static void SeedHealth(ICoreDbFactory dbf, string svc, string tags, string status = "ok")
	{
		using var db = dbf.Open();
		db.Insert(new HealthReport { Svc = svc, Tags = tags, Status = status, ReceivedAt = DateTime.UtcNow, Source = "push" });
	}

	static void SeedSetting(ICoreDbFactory dbf, string scope, string scopeKey, string path)
	{
		using var db = dbf.Open();
		db.Insert(new Setting { Scope = scope, ScopeKey = scopeKey, Path = path, Type = "String", Value = "v", UpdatedAt = DateTime.UtcNow });
	}

	[Fact]
	public async Task Admin_rollup_counts_the_whole_fleet_in_one_pass()
	{
		var (svc, dbf) = New();
		// A fresh core.db already carries the built-in $system workspace/project (seeded by
		// migration) — count the DELTA this test adds rather than an absolute, so a future migration
		// that seeds one more built-in row does not make this test lie about what it is checking.
		var before = await svc.GetAdminRollupAsync();

		SeedWorkspace(dbf, "alpha");
		SeedWorkspace(dbf, "beta");
		SeedProject(dbf, "app", "alpha");
		// A workspace memory container is a real Projects row — the admin count includes it,
		// matching the pre-existing page's unfiltered db.Projects.CountAsync().
		SeedProject(dbf, "$ws-alpha", "alpha");
		SeedUser(dbf, "u1");
		SeedUser(dbf, "u2");
		SeedApiKey(dbf, "k1", "app");
		SeedSetting(dbf, "System", "$", "a.b");
		// Not counted: a non-System-scope override (project/user scope is a different page's number).
		SeedSetting(dbf, "Project", "app", "a.b");

		var rollup = await svc.GetAdminRollupAsync();

		(rollup.WorkspaceCount - before.WorkspaceCount).Should().Be(2);
		(rollup.ProjectCount - before.ProjectCount).Should().Be(2, "the sysadmin count includes the memory container, unlike IProjectDirectory");
		(rollup.UserCount - before.UserCount).Should().Be(2);
		(rollup.SettingOverrideCount - before.SettingOverrideCount).Should().Be(1, "only System-scope rows count here");
		(rollup.AgentKeyCount - before.AgentKeyCount).Should().Be(1);
	}

	[Fact]
	public async Task Workspace_rollup_scopes_counts_to_exactly_the_given_projects()
	{
		var (svc, dbf) = New();
		SeedWorkspace(dbf, "alpha");
		SeedProject(dbf, "app", "alpha");
		SeedProject(dbf, "other", "alpha");
		SeedApiKey(dbf, "k1", "app");
		SeedApiKey(dbf, "k2", "app");
		SeedApiKey(dbf, "k3", "other");
		SeedDataDb(dbf, "app", "main");
		SeedLog(dbf, "app", "default");

		var rollup = await svc.GetWorkspaceRollupAsync(["app"]);

		rollup.KeyCount["app"].Should().Be(2);
		rollup.KeyCount.Should().NotContainKey("other", "a project not in the requested set is not in the result");
		rollup.DbCount["app"].Should().Be(1);
		rollup.LogCount["app"].Should().Be(1);
	}

	[Fact]
	public async Task Workspace_rollup_health_is_not_sql_scoped_the_caller_filters_it()
	{
		// Same shape as the page code this replaces: the project tag lives inside Tags (not a
		// queryable column), so the read is NOT narrowed by projectKeys in SQL — every latest report
		// across the whole fleet comes back, and the CALLER (the page) is the one that filters by its
		// own projectKeys before rendering. This test pins that contract so a future "optimization"
		// that adds a SQL filter here does not silently start hiding cross-workspace reports that a
		// caller's own filter still expects to see (and then drop).
		var (svc, dbf) = New();
		SeedHealth(dbf, "svc1", "project:app");
		SeedHealth(dbf, "svc1", "project:someone-elses-project");

		var rollup = await svc.GetWorkspaceRollupAsync(["app"]);

		rollup.LatestHealthReports.Select(r => r.Tags).Should()
			.Contain("project:someone-elses-project", "the service hands back the unfiltered latest set, exactly like the inline code it replaced");
	}

	[Fact]
	public async Task Project_rollup_counts_exactly_one_project_and_groups_health_by_raw_tags()
	{
		var (svc, dbf) = New();
		SeedProject(dbf, "app", "alpha");
		SeedApiKey(dbf, "k1", "app");
		SeedApiKey(dbf, "k2", "other");
		SeedDataDb(dbf, "app", "main");
		SeedLog(dbf, "app", "default");
		// Two reports, same Svc, DIFFERENT raw Tags — ProjectHome's grouping key is (Svc, Tags), so
		// both survive as "latest" (unlike the workspace rollup's (Svc, parsed project) grouping,
		// which would have collapsed them).
		SeedHealth(dbf, "svc1", "project:app,host:a");
		SeedHealth(dbf, "svc1", "project:app,host:b");

		var rollup = await svc.GetProjectRollupAsync("app");

		rollup.KeyCount.Should().Be(1);
		rollup.DbCount.Should().Be(1);
		rollup.LogCount.Should().Be(1);
		rollup.LatestHealth.Should().HaveCount(2, "grouped by (Svc, raw Tags) — distinct Tags strings are distinct rows");
	}
}
