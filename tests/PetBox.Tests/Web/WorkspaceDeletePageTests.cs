using System.Security.Claims;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using PetBox.Core.Data;
using PetBox.Core.Models;
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

	WorkspacesModel Page()
	{
		var page = new WorkspacesModel(_db.Factory(), new WorkspaceProvisioning(_db.Factory()));
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

	[Fact]
	public async Task Delete_empty_workspace_removes_it_and_its_memberships()
	{
		_db.Insert(new Workspace { Key = "solo", Name = "Solo", Description = "", CreatedAt = DateTime.UtcNow });
		var uid = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "u2", PasswordHash = "x", CreatedAt = DateTime.UtcNow });
		_db.Insert(new WorkspaceMember { UserId = uid, WorkspaceKey = "solo", Role = WorkspaceRole.Admin });

		var page = Page();
		var result = await page.OnPostDeleteAsync("solo");

		result.Should().BeOfType<RedirectToPageResult>();
		_db.Workspaces.Any(w => w.Key == "solo").Should().BeFalse();
		_db.WorkspaceMembers.Any(m => m.WorkspaceKey == "solo").Should().BeFalse("empty-ws delete cleans memberships");
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
