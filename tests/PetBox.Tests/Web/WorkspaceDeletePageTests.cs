using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Auth;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Web;

// ui-workspace-delete-cascade: deleting a workspace must NOT orphan data. A workspace that
// still has projects is REJECTED (delete/move them first); an empty workspace deletes cleanly
// and takes its memberships with it. $system stays undeletable.
public sealed class WorkspaceDeletePageTests : IDisposable
{
	readonly string _dir;
	readonly PetBoxDb _db;

	public WorkspaceDeletePageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-wsdel-" + Guid.NewGuid().ToString("N"));
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

	// The page holds no connection any more — the gate, the cascade and the create all live in
	// IWorkspaceAdminService (AGENTS.md: the database is visible only in the service layer). The
	// fixture therefore composes the REAL service over the same core.db, so these tests still exercise
	// the production write path end to end; not a single assertion below changed.
	WorkspaceProvisioning Provisioning() =>
		new(_db.Factory(), new WorkspaceMembershipService(_db.Factory()));

	WorkspacesModel Page()
	{
		var dbf = _db.Factory();
		var members = new WorkspaceMembershipService(dbf);
		var page = new WorkspacesModel(new WorkspaceAdminService(
			dbf, new ProjectDirectory(dbf), members, new WorkspaceProvisioning(dbf, members)));
		var http = new DefaultHttpContext
		{
			User = new ClaimsPrincipal(new ClaimsIdentity(
				[new Claim(PetBox.Core.Auth.PetBoxClaims.UserId, "1")], "test")),
		};
		page.PageContext = new PageContext { HttpContext = http };
		page.TempData = new TempDataDictionary(http, new EmptyTempDataProvider());
		return page;
	}

	sealed class EmptyTempDataProvider : ITempDataProvider
	{
		public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
		public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
	}

	[Fact]
	public async Task Delete_nonempty_workspace_is_rejected_and_nothing_is_removed()
	{
		_db.Insert(new Workspace { Key = "acme", Name = "Acme", Description = "", CreatedAt = DateTime.UtcNow });
		_db.Insert(new Project { Key = "web", WorkspaceKey = "acme", Name = "Web", Description = "" });
		var uid = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "u1", PasswordHash = "x", CreatedAt = DateTime.UtcNow });
		_db.Insert(new WorkspaceMember { UserId = uid, WorkspaceKey = "acme", Role = WorkspaceRole.Admin });

		var page = Page();
		var result = await page.OnPostDeleteAsync("acme");

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().Contain("1 project");
		_db.Workspaces.Any(w => w.Key == "acme").Should().BeTrue("a non-empty workspace is not deleted");
		_db.WorkspaceMembers.Any(m => m.WorkspaceKey == "acme").Should().BeTrue("memberships are untouched on rejection");
	}

	// workspace-delete-blocked-by-own-container: the workspace is created through the PRODUCTION
	// path (WorkspaceProvisioning.CreateAsync), so it carries the `$ws-<key>` memory container that
	// EnsureContainerAsync provisions with every workspace. That container is a Projects row, and
	// the has-projects gate used to count it — which made EVERY workspace permanently undeletable.
	// Building the fixture with a bare `_db.Insert(new Workspace …)` is what hid the bug: no
	// container, so the gate had nothing to miscount and the test stayed green on broken code.
	[Fact]
	public async Task Delete_empty_workspace_removes_it_its_container_and_its_memberships()
	{
		var uid = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "u2", PasswordHash = "x", CreatedAt = DateTime.UtcNow, WorkspaceQuota = 1 });

		var created = await Provisioning()
			.CreateAsync("solo", "Solo", "", uid, bypassQuota: false);
		created.Ok.Should().BeTrue();
		_db.Projects.Any(p => p.Key == "$ws-solo").Should().BeTrue("the production create path provisions the container");

		var page = Page();
		var result = await page.OnPostDeleteAsync("solo");

		result.Should().BeOfType<RedirectToPageResult>();
		_db.Workspaces.Any(w => w.Key == "solo").Should().BeFalse();
		_db.Projects.Any(p => p.WorkspaceKey == "solo").Should().BeFalse("the ws memory container dies with its workspace");
		_db.WorkspaceMembers.Any(m => m.WorkspaceKey == "solo").Should().BeFalse("empty-ws delete cleans memberships");
	}

	// A deleted workspace must give its quota slot back — the allowance counts owned workspaces, so
	// an orphaned Admin membership would turn a limit of 1 into a one-shot ticket.
	[Fact]
	public async Task Deleting_a_workspace_frees_the_owner_quota_slot()
	{
		var uid = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "u3", PasswordHash = "x", CreatedAt = DateTime.UtcNow, WorkspaceQuota = 1 });
		var provisioning = Provisioning();

		(await provisioning.CreateAsync("first", "First", "", uid, bypassQuota: false)).Ok.Should().BeTrue();
		(await provisioning.CanCreateAsync(uid)).Should().BeFalse("the allowance of 1 is spent");

		(await Page().OnPostDeleteAsync("first")).Should().BeOfType<RedirectToPageResult>();

		(await provisioning.CanCreateAsync(uid)).Should().BeTrue("deleting the workspace returns the slot");
		(await provisioning.CreateAsync("second", "Second", "", uid, bypassQuota: false))
			.Ok.Should().BeTrue("and the owner can create a new workspace with it");
	}

	// The gate itself must survive the fix: a REAL user project still blocks the delete. The
	// container is not a project for this purpose; `web` is.
	[Fact]
	public async Task Delete_workspace_with_a_real_project_is_still_rejected_despite_the_container()
	{
		var uid = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "u4", PasswordHash = "x", CreatedAt = DateTime.UtcNow, WorkspaceQuota = 1 });
		(await Provisioning()
			.CreateAsync("corp", "Corp", "", uid, bypassQuota: false)).Ok.Should().BeTrue();
		_db.Insert(new Project { Key = "web", WorkspaceKey = "corp", Name = "Web", Description = "" });

		var page = Page();
		var result = await page.OnPostDeleteAsync("corp");

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().Contain("1 project", "the container is not counted, the real project is");
		_db.Workspaces.Any(w => w.Key == "corp").Should().BeTrue();
		_db.Projects.Any(p => p.Key == "$ws-corp").Should().BeTrue("a rejected delete removes nothing");
	}

	[Fact]
	public async Task Delete_system_workspace_is_refused()
	{
		var page = Page();
		var result = await page.OnPostDeleteAsync("$system");

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().Contain("$system");
		_db.Workspaces.Any(w => w.Key == "$system").Should().BeTrue();
	}

	// workspace-memory-isolation: create path allowlists keys that become URL/file segments.
	[Theory]
	[InlineData("a:b")]
	[InlineData("a b")]
	[InlineData("a/b")]
	[InlineData("sys")]
	[InlineData("$system")] // also pre-seeded; create must still reject (allowlist)
	[InlineData("Foo")]
	public async Task Create_rejects_invalid_workspace_key(string key)
	{
		var page = Page();
		var before = _db.Workspaces.Count();
		var result = await page.OnPostCreateAsync(key, "Name", "desc");

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().Contain("must match");
		_db.Workspaces.Count().Should().Be(before, "rejected create must not insert a new workspace row");
	}

	[Fact]
	public async Task Create_accepts_allowlisted_key_and_provisions_memory_container()
	{
		var page = Page();
		// WorkspacesModel create needs a user claim for auto-membership — optional for this assert.
		var result = await page.OnPostCreateAsync("acme-1", "Acme", "desc");

		result.Should().BeOfType<RedirectToPageResult>();
		_db.Workspaces.Any(w => w.Key == "acme-1").Should().BeTrue();
		_db.Projects.Any(p => p.Key == "$ws-acme-1" && p.WorkspaceKey == "acme-1").Should().BeTrue();
	}
}
