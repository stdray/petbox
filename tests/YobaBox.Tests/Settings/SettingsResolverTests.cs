using LinqToDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Core.Settings;

namespace YobaBox.Tests.Settings;

[Collection("WebAppFactory")]
public sealed class SettingsResolverTests : IAsyncLifetime
{
	readonly WebApplicationFactory<Program> _factory;
	const string TestPasswordHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	public SettingsResolverTests()
	{
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__YOBOBOX", "Data Source=:memory:;Cache=Shared");
		Environment.SetEnvironmentVariable("YOBABOX_MASTER_KEY", "test-key-for-secrets");
		_factory = new WebApplicationFactory<Program>()
			.WithWebHostBuilder(b =>
			{
				b.UseEnvironment("Testing");
				b.ConfigureAppConfiguration((_, cfg) =>
				{
					cfg.AddInMemoryCollection(new Dictionary<string, string?>
					{
						["Admin:Username"] = "admin",
						["Admin:PasswordHash"] = TestPasswordHash,
					});
				});
			});
	}

	public async Task InitializeAsync()
	{
		// Shared in-memory DB across the WebAppFactory collection means previous
		// tests may have inserted Setting rows. Wipe to avoid cross-test cascade leaks.
		using var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<YobaBoxDb>();
		await db.Settings.DeleteAsync();
	}

	public Task DisposeAsync()
	{
		_factory.Dispose();
		Environment.SetEnvironmentVariable("CONNECTIONSTRINGS__YOBOBOX", null);
		Environment.SetEnvironmentVariable("YOBABOX_MASTER_KEY", null);
		return Task.CompletedTask;
	}

	// Test records — defined as nested types to keep them local to these tests.

	public sealed record TestLogSettings
	{
		[Setting(TopLevel = Scope.Workspace, Key = "test.log.retention.days")]
		public int RetentionDays { get; init; } = 20;

		[Setting(TopLevel = Scope.System, Key = "test.log.retention.size")]
		public long RetentionSize { get; init; } = 40_000_000;
	}

	public sealed record TestUiSettings
	{
		[Setting(TopLevel = Scope.User, Key = "test.ui.theme")]
		public string Theme { get; init; } = "dark";
	}

	public sealed record TestSecretSettings
	{
		[Setting(TopLevel = Scope.User, Key = "test.secret.key", IsSecret = true)]
		public string ApiKey { get; init; } = "";
	}

	async Task<(ISettingsResolver Resolver, YobaBoxDb Db)> GetResolverAsync(string? projectKey = null, string? workspaceKey = null)
	{
		var scope = _factory.Services.CreateScope();
		var db = scope.ServiceProvider.GetRequiredService<YobaBoxDb>();

		if (workspaceKey is not null && !db.Workspaces.Any(w => w.Key == workspaceKey))
			await db.InsertAsync(new Workspace { Key = workspaceKey, Name = workspaceKey, CreatedAt = DateTime.UtcNow });

		if (projectKey is not null && workspaceKey is not null && !db.Projects.Any(p => p.Key == projectKey))
			await db.InsertAsync(new Project { Key = projectKey, WorkspaceKey = workspaceKey, Name = projectKey });

		var resolver = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
		return (resolver, db);
	}

	[Fact]
	public async Task Get_NoRows_ReturnsRecordDefaults()
	{
		var (resolver, _) = await GetResolverAsync(projectKey: "proj-default", workspaceKey: "ws-default");

		var result = await resolver.GetAsync<TestLogSettings>(Scope.Project, "proj-default");

		result.RetentionDays.Should().Be(20);
		result.RetentionSize.Should().Be(40_000_000);
	}

	[Fact]
	public async Task Get_ProjectRow_Wins()
	{
		var (resolver, db) = await GetResolverAsync(projectKey: "proj-pwin", workspaceKey: "ws-pwin");
		await db.InsertAsync(new Setting
		{
			Scope = "Project",
			ScopeKey = "proj-pwin",
			Path = "test.log.retention.days",
			Type = "int",
			Value = "5",
			UpdatedAt = DateTime.UtcNow,
		});

		var result = await resolver.GetAsync<TestLogSettings>(Scope.Project, "proj-pwin");

		result.RetentionDays.Should().Be(5);
	}

	[Fact]
	public async Task Get_Cascade_WorkspaceWhenProjectMissing()
	{
		var (resolver, db) = await GetResolverAsync(projectKey: "proj-casc", workspaceKey: "ws-casc");
		await db.InsertAsync(new Setting
		{
			Scope = "Workspace",
			ScopeKey = "ws-casc",
			Path = "test.log.retention.days",
			Type = "int",
			Value = "60",
			UpdatedAt = DateTime.UtcNow,
		});

		var result = await resolver.GetAsync<TestLogSettings>(Scope.Project, "proj-casc");

		result.RetentionDays.Should().Be(60);
	}

	[Fact]
	public async Task Get_Cascade_SystemWhenWorkspaceAndProjectMissing()
	{
		var (resolver, db) = await GetResolverAsync(projectKey: "proj-sysfall", workspaceKey: "ws-sysfall");
		await db.InsertAsync(new Setting
		{
			Scope = "System",
			ScopeKey = "$",
			Path = "test.log.retention.size",
			Type = "long",
			Value = "999",
			UpdatedAt = DateTime.UtcNow,
		});

		var result = await resolver.GetAsync<TestLogSettings>(Scope.Project, "proj-sysfall");

		result.RetentionSize.Should().Be(999);
	}

	[Fact]
	public async Task Get_TopLevelCap_DoesNotReadAboveCap()
	{
		// TestLogSettings.RetentionDays has TopLevel=Workspace.
		// System row should NOT be read because it's above the cap.
		var (resolver, db) = await GetResolverAsync(projectKey: "proj-cap", workspaceKey: "ws-cap");
		await db.InsertAsync(new Setting
		{
			Scope = "System",
			ScopeKey = "$",
			Path = "test.log.retention.days",
			Type = "int",
			Value = "999",
			UpdatedAt = DateTime.UtcNow,
		});

		var result = await resolver.GetAsync<TestLogSettings>(Scope.Project, "proj-cap");

		// System row ignored; record default is used.
		result.RetentionDays.Should().Be(20);
	}

	[Fact]
	public async Task Set_CreatesRowForChangedProperty()
	{
		var (resolver, db) = await GetResolverAsync(projectKey: "proj-set", workspaceKey: "ws-set");
		var oldVals = new TestLogSettings();
		var newVals = oldVals with { RetentionDays = 7 };

		await resolver.SetAsync(Scope.Project, "proj-set", newVals, oldVals, updatedBy: 42);

		var row = db.Settings.FirstOrDefault(s =>
			s.Scope == "Project" && s.ScopeKey == "proj-set" && s.Path == "test.log.retention.days");
		row.Should().NotBeNull();
		row!.Value.Should().Be("7");
		row.Type.Should().Be("int");
		row.UpdatedBy.Should().Be(42);
	}

	[Fact]
	public async Task Set_DoesNotWriteUnchangedProperty()
	{
		var (resolver, db) = await GetResolverAsync(projectKey: "proj-noop", workspaceKey: "ws-noop");
		var oldVals = new TestLogSettings();
		var newVals = oldVals; // no change

		await resolver.SetAsync(Scope.Project, "proj-noop", newVals, oldVals, updatedBy: null);

		var rows = db.Settings
			.Where(s => s.ScopeKey == "proj-noop")
			.ToList();
		rows.Should().BeEmpty();
	}

	[Fact]
	public async Task Set_UpdatesExistingRow()
	{
		var (resolver, db) = await GetResolverAsync(projectKey: "proj-upd", workspaceKey: "ws-upd");
		await db.InsertAsync(new Setting
		{
			Scope = "Project",
			ScopeKey = "proj-upd",
			Path = "test.log.retention.days",
			Type = "int",
			Value = "10",
			UpdatedAt = DateTime.UtcNow,
		});

		var oldVals = new TestLogSettings { RetentionDays = 10 };
		var newVals = oldVals with { RetentionDays = 50 };

		await resolver.SetAsync(Scope.Project, "proj-upd", newVals, oldVals, updatedBy: null);

		var rows = db.Settings
			.Where(s => s.ScopeKey == "proj-upd" && s.Path == "test.log.retention.days")
			.ToList();
		rows.Should().HaveCount(1);
		rows[0].Value.Should().Be("50");
	}

	[Fact]
	public async Task Reset_DeletesOverrideRow()
	{
		var (resolver, db) = await GetResolverAsync(projectKey: "proj-rst", workspaceKey: "ws-rst");
		await db.InsertAsync(new Setting
		{
			Scope = "Project",
			ScopeKey = "proj-rst",
			Path = "test.log.retention.days",
			Type = "int",
			Value = "99",
			UpdatedAt = DateTime.UtcNow,
		});

		await resolver.ResetAsync<TestLogSettings>(Scope.Project, "proj-rst", nameof(TestLogSettings.RetentionDays));

		var rows = db.Settings
			.Where(s => s.ScopeKey == "proj-rst" && s.Path == "test.log.retention.days")
			.ToList();
		rows.Should().BeEmpty();

		// Read after reset returns record default.
		var result = await resolver.GetAsync<TestLogSettings>(Scope.Project, "proj-rst");
		result.RetentionDays.Should().Be(20);
	}

	[Fact]
	public async Task Secret_RoundTrip()
	{
		var (resolver, db) = await GetResolverAsync();
		var oldVals = new TestSecretSettings();
		var newVals = oldVals with { ApiKey = "sk-test-12345" };

		await resolver.SetAsync(Scope.User, "user-1", newVals, oldVals, updatedBy: 1);

		// Stored value is NOT the plaintext.
		var stored = db.Settings.FirstOrDefault(s =>
			s.Scope == "User" && s.ScopeKey == "user-1" && s.Path == "test.secret.key");
		stored.Should().NotBeNull();
		stored!.Type.Should().Be("secret");
		stored.Value.Should().NotContain("sk-test-12345");

		// Reading decrypts.
		var loaded = await resolver.GetAsync<TestSecretSettings>(Scope.User, "user-1");
		loaded.ApiKey.Should().Be("sk-test-12345");
	}

	[Fact]
	public async Task User_NoCascadeToWorkspace()
	{
		// TestUiSettings.Theme has TopLevel=User. Chain from User is User → System only.
		// A Workspace row should NOT match.
		var (resolver, db) = await GetResolverAsync(workspaceKey: "ws-ui");
		await db.InsertAsync(new Setting
		{
			Scope = "Workspace",
			ScopeKey = "ws-ui",
			Path = "test.ui.theme",
			Type = "string",
			Value = "light",
			UpdatedAt = DateTime.UtcNow,
		});

		var result = await resolver.GetAsync<TestUiSettings>(Scope.User, "user-7");

		// Workspace row ignored — User chain doesn't include Workspace.
		result.Theme.Should().Be("dark");
	}

	[Fact]
	public async Task Bool_RoundTrip()
	{
		var (resolver, db) = await GetResolverAsync();
		await db.InsertAsync(new Setting
		{
			Scope = "User",
			ScopeKey = "user-bool",
			Path = "test.misc.flag",
			Type = "bool",
			Value = "true",
			UpdatedAt = DateTime.UtcNow,
		});

		// Read via a record that declares this property.
		var loaded = await resolver.GetAsync<BoolSettings>(Scope.User, "user-bool");
		loaded.Flag.Should().BeTrue();
	}

	public sealed record BoolSettings
	{
		[Setting(TopLevel = Scope.User, Key = "test.misc.flag")]
		public bool Flag { get; init; }
	}
}
