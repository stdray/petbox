using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Configuration;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;
using PetBox.Web.Pages;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Web;

// ui-mutation-feedback-consistency: successful mutating POSTs must Post/Redirect/Get to a clean
// URL (no lingering ?handler=) and carry a one-line success notice (or the once-shown minted key)
// across the redirect via TempData — the shared Notice mechanism. These exercise the write side
// of that mechanism at the handler level; ModuleViewsTests covers the render-after-redirect.
public sealed class MutationFeedbackPageTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;

	public MutationFeedbackPageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-mutfb-" + Guid.NewGuid().ToString("N"));
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

	static FeatureFlags Features() =>
		new(new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Data"] = "true" })
			.Build());

	// Give a bare PageModel a TempData bag (and a claims user), so mutating handlers that call
	// NotifySuccess/CarryNewKey — and any that read User — run outside a real request pipeline.
	static void Wire(PageModel page, long userId = 42)
	{
		var http = new DefaultHttpContext
		{
			User = new ClaimsPrincipal(new ClaimsIdentity(
				[new Claim(PetBoxClaims.UserId, userId.ToString())], "test")),
		};
		page.PageContext = new PageContext { HttpContext = http };
		page.TempData = new TempDataDictionary(http, new NoopTempDataProvider());
	}

	[Fact]
	public async Task Workspace_create_redirects_clean_and_sets_success_notice()
	{
		var page = new WorkspacesModel(_db.Factory());
		Wire(page);

		var result = await page.OnPostCreateAsync("acme", "Acme", "desc");

		var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
		redirect.PageHandler.Should().BeNull("PRG lands on a clean URL, not a ?handler= one");
		page.TempData[Notice.SuccessKey].Should().Be("Workspace 'acme' created.");
		_db.Workspaces.Any(w => w.Key == "acme").Should().BeTrue();
	}

	[Fact]
	public async Task Workspace_delete_redirects_clean_and_sets_success_notice()
	{
		_db.Insert(new Workspace { Key = "solo", Name = "Solo", Description = "", CreatedAt = DateTime.UtcNow });
		var page = new WorkspacesModel(_db.Factory());
		Wire(page);

		var result = await page.OnPostDeleteAsync("solo");

		result.Should().BeOfType<RedirectToPageResult>();
		page.TempData[Notice.SuccessKey].Should().Be("Workspace 'solo' deleted.");
		_db.Workspaces.Any(w => w.Key == "solo").Should().BeFalse();
	}

	[Fact]
	public async Task User_delete_redirects_clean_and_sets_success_notice()
	{
		var cfg = new ConfigurationBuilder().Build();
		var adminOptions = Microsoft.Extensions.Options.Options.Create(new AdminOptions());
		var victimId = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "victim", PasswordHash = "x", CreatedAt = DateTime.UtcNow });

		var page = new UsersModel(_db.Factory(), adminOptions);
		Wire(page, userId: 999); // current user is someone else, so the self-delete guard passes

		var result = await page.OnPostDeleteAsync(victimId);

		result.Should().BeOfType<RedirectToPageResult>();
		page.TempData[Notice.SuccessKey].Should().Be("User 'victim' deleted.");
		_db.Users.Any(u => u.Id == victimId).Should().BeFalse();
	}

	[Fact]
	public async Task Connect_mint_redirects_clean_and_carries_the_once_shown_key()
	{
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		var page = new ProjectConnectModel(_db.Factory(), Features()) { WorkspaceKey = "ws", ProjectKey = "proj" };
		Wire(page);

		var result = await page.OnPostMintAsync("agent", [ApiKeyScopes.TasksRead]);

		var redirect = result.Should().BeOfType<RedirectToPageResult>().Subject;
		redirect.PageHandler.Should().BeNull("a refresh of the clean URL must not re-POST and mint again");
		page.TempData[Notice.NewKeyKey].Should().NotBeNull("the one-time key rides across the redirect");
		_db.ApiKeys.Count(k => k.ProjectKey == "proj").Should().Be(1);
	}

	[Fact]
	public async Task ProjectDetail_create_key_redirects_to_clean_url_and_carries_the_key()
	{
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		var page = new ProjectDetailModel(_db.Factory(), Features(), new NullSettingsResolver())
		{
			WorkspaceKey = "ws",
			ProjectKey = "proj",
		};
		Wire(page);

		var result = await page.OnPostCreateKeyAsync("ci", [ApiKeyScopes.TasksRead]);

		var redirect = result.Should().BeOfType<RedirectResult>().Subject;
		redirect.Url.Should().NotContain("handler=", "PRG lands on the clean project URL");
		page.TempData[Notice.NewKeyKey].Should().NotBeNull();
		_db.ApiKeys.Count(k => k.ProjectKey == "proj").Should().Be(1);
	}

	[Fact]
	public async Task ProjectDetail_create_key_with_blank_name_re_renders_and_carries_nothing()
	{
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		var page = new ProjectDetailModel(_db.Factory(), Features(), new NullSettingsResolver())
		{
			WorkspaceKey = "ws",
			ProjectKey = "proj",
		};
		Wire(page);

		var result = await page.OnPostCreateKeyAsync("  ", [ApiKeyScopes.TasksRead]);

		result.Should().BeOfType<PageResult>("a validation failure re-renders with the form, not a PRG");
		page.TempData[Notice.NewKeyKey].Should().BeNull();
		page.ErrorMessage.Should().NotBeNullOrEmpty();
		_db.ApiKeys.Count(k => k.ProjectKey == "proj").Should().Be(0);
	}

	// notice-tail: the remaining mutators that already Post/Redirect/Get now also carry a
	// one-line success notice across the redirect. A representative few of them exercised here.
	[Fact]
	public async Task AgentKeys_revoke_redirects_clean_and_sets_success_notice()
	{
		_db.Insert(new ApiKey { Key = "yb_key_x", ProjectKey = "proj", Scopes = ApiKeyScopes.TasksRead, Name = "ci", CreatedAt = DateTime.UtcNow });
		var page = new AgentKeysModel(new PetBox.Web.Auth.AgentKeyAdminService(_db.Factory()));
		Wire(page);

		var result = await page.OnPostRevokeAsync("yb_key_x");

		result.Should().BeOfType<RedirectToPageResult>();
		page.TempData[Notice.SuccessKey].Should().Be("API key revoked.");
		_db.ApiKeys.Any(k => k.Key == "yb_key_x").Should().BeFalse();
	}

	[Fact]
	public async Task WorkspaceUsers_remove_redirects_clean_and_sets_success_notice()
	{
		var uid = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "bob", PasswordHash = "x", CreatedAt = DateTime.UtcNow });
		_db.Insert(new WorkspaceMember { UserId = uid, WorkspaceKey = "ws", Role = WorkspaceRole.Member });
		var page = new WorkspaceUsersModel(_db.Factory());
		Wire(page);

		var result = await page.OnPostRemoveAsync("ws", uid);

		result.Should().BeOfType<RedirectToPageResult>();
		page.TempData[Notice.SuccessKey].Should().Be("Member removed.");
		_db.WorkspaceMembers.Any(m => m.UserId == uid && m.WorkspaceKey == "ws").Should().BeFalse();
	}

	[Fact]
	public async Task ProjectDetail_revoke_key_redirects_clean_and_sets_success_notice()
	{
		_db.Insert(new Project { Key = "proj", WorkspaceKey = "ws", Name = "P", Description = "" });
		_db.Insert(new ApiKey { Key = "yb_key_z", ProjectKey = "proj", Scopes = ApiKeyScopes.TasksRead, Name = "ci", CreatedAt = DateTime.UtcNow });
		var page = new ProjectDetailModel(_db.Factory(), Features(), new NullSettingsResolver())
		{
			WorkspaceKey = "ws",
			ProjectKey = "proj",
		};
		Wire(page);

		var result = await page.OnPostRevokeKeyAsync("yb_key_z");

		var redirect = result.Should().BeOfType<RedirectResult>().Subject;
		redirect.Url.Should().NotContain("handler=", "PRG lands on the clean project URL");
		page.TempData[Notice.SuccessKey].Should().Be("API key revoked.");
		_db.ApiKeys.Any(k => k.Key == "yb_key_z").Should().BeFalse();
	}

	// TempData provider that neither loads nor persists — enough for unit tests that only read
	// back what a handler just wrote into the in-memory bag.
	sealed class NoopTempDataProvider : ITempDataProvider
	{
		public IDictionary<string, object> LoadTempData(HttpContext context) =>
			new Dictionary<string, object>();

		public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
	}
}
