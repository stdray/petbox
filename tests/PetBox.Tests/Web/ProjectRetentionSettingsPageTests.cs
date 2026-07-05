using LinqToDB;
using LinqToDB.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using PetBox.Config;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Web;
using PetBox.Web.Pages.Admin;
using PetBox.Web.Settings;

namespace PetBox.Tests.Web;

// Log retention lives ONLY on the project Info page (/info) — card ui-log-retention-settings-fix.
// Two regressions are locked here:
//   1. /info hint must tell the truth: an ACTIVE override is never labelled the "system default".
//      The page model exposes EffectiveRetentionDays (override wins) AND DefaultRetentionDays
//      (the fallback resolved from ABOVE the project, ignoring the override) so the view can show
//      both honestly.
//   2. The project-scope /log page had no fields yet reported a false "saved" and re-POSTed on
//      refresh. It is now a pure redirect to /info (GET-only, no Save handler).
public sealed class ProjectRetentionSettingsPageTests : IDisposable
{
	const string Ws = "ws";
	const string Proj = "proj";
	readonly string _dir;
	readonly PetBoxDb _db;

	public ProjectRetentionSettingsPageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-retention-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_dir);
		var cs = $"Data Source={Path.Combine(_dir, "petbox.db")}";
		TestSchema.Core(cs);
		_db = new PetBoxDb(PetBoxDb.CreateOptions(cs));
		_db.Insert(new Workspace { Key = Ws, Name = Ws, CreatedAt = DateTime.UtcNow });
		_db.Insert(new Project { Key = Proj, WorkspaceKey = Ws, Name = "P", Description = "" });
	}

	public void Dispose()
	{
		_db.Dispose();
		TestDirs.CleanupOrDefer(_dir);
	}

	// The resolver only touches ISecretEncryptor for IsSecret properties; LogSettings has none,
	// so a throwing stub is safe and keeps the test free of a master key.
	sealed class NoSecrets : ISecretEncryptor
	{
		public bool IsAvailable => false;
		public SecretBundle Encrypt(string plaintext) => throw new NotSupportedException();
		public string Decrypt(string ciphertextB64, string ivB64, string authTagB64) => throw new NotSupportedException();
	}

	static FeatureFlags Features() =>
		new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

	ProjectDetailModel InfoPage() =>
		new(_db, Features(), new SettingsResolver(_db, new NoSecrets())) { WorkspaceKey = Ws, ProjectKey = Proj };

	void SetSetting(string scope, string scopeKey, string value) =>
		_db.Insert(new Setting
		{
			Scope = scope,
			ScopeKey = scopeKey,
			Path = "log.retention.days",
			Type = "int",
			Value = value,
			UpdatedAt = DateTime.UtcNow,
		});

	[Fact]
	public async Task Hint_NoOverride_UsesSystemDefault()
	{
		var page = InfoPage();
		await page.OnGetAsync();

		// No project row → the effective value IS the default; nothing to disambiguate.
		page.RetentionOverrideDays.Should().BeNull();
		page.EffectiveRetentionDays.Should().Be(7); // LogSettings.RetentionDays record default
		page.DefaultRetentionDays.Should().Be(page.EffectiveRetentionDays);
	}

	[Fact]
	public async Task Hint_ActiveOverride_ExposesOverrideAndTrueDefaultSeparately()
	{
		// Workspace default 14, project override 3. The effective value is the override, but the
		// hint must still be able to name the TRUE fallback default (14) — never call 3 the default.
		SetSetting("Workspace", Ws, "14");
		SetSetting("Project", Proj, "3");

		var page = InfoPage();
		await page.OnGetAsync();

		page.RetentionOverrideDays.Should().Be(3);
		page.EffectiveRetentionDays.Should().Be(3);
		page.DefaultRetentionDays.Should().Be(14);
		// The bug was EffectiveRetentionDays being shown under the "system default" label.
		page.DefaultRetentionDays.Should().NotBe(page.EffectiveRetentionDays);
	}

	[Fact]
	public void LogPage_Get_RedirectsToInfo_NoFormNoFalseSuccess()
	{
		var page = new ProjectLogSettingsModel { WorkspaceKey = Ws, ProjectKey = Proj };

		var result = page.OnGet();

		var redirect = result.Should().BeOfType<RedirectResult>().Subject;
		redirect.Url.Should().Be(Routes.ProjectSettings(Ws, Proj));
		redirect.Url.Should().EndWith("/info");
	}

	[Fact]
	public void LogPage_HasNoSaveHandler()
	{
		// The empty project-scope form and its false "Log settings saved." no-op POST are gone.
		typeof(ProjectLogSettingsModel).GetMethod("OnPostSaveAsync").Should().BeNull();
		typeof(ProjectLogSettingsModel).GetMethod("OnPostSave").Should().BeNull();
	}
}
