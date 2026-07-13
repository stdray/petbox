using LinqToDB;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Pages.Admin;

namespace PetBox.Tests.Web;

// Add-member flow (work card ui-addmember-password-required): adding an EXISTING user must
// not require or overwrite a password; creating a NEW user requires one (empty PasswordHash is
// not loginable — see M008_Users) and surfaces a visible ModelState/ErrorMessage, not a native
// off-screen validation bubble.
public sealed class WorkspaceUsersPageTests : IDisposable
{
	const string Ws = "$system";
	const string ExistingHash = "pbkdf2$100000$h1twJi/he3s8S7jSM9pkGQ==$efnLBffww5Gprn6BjpNgZkTcG+1zNu2L6z3TZ7YvD/o=";

	readonly string _dir;
	readonly PetBoxDb _db;

	public WorkspaceUsersPageTests()
	{
		_dir = Path.Combine(Path.GetTempPath(), "petbox-wsusers-" + Guid.NewGuid().ToString("N"));
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

	WorkspaceUsersModel Page() => new(_db.Factory());

	[Fact]
	public async Task Add_existing_user_without_password_succeeds_and_keeps_password()
	{
		var userId = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "alice", PasswordHash = ExistingHash, CreatedAt = DateTime.UtcNow });

		var page = Page();
		var result = await page.OnPostAddAsync(Ws, "alice", Password: null, WorkspaceRole.Member);

		result.Should().BeOfType<RedirectToPageResult>();
		page.ErrorMessage.Should().BeNull();

		_db.WorkspaceMembers.Count(m => m.UserId == userId && m.WorkspaceKey == Ws).Should().Be(1);
		// Password must not be touched for an existing account.
		_db.Users.First(u => u.Id == userId).PasswordHash.Should().Be(ExistingHash);
		// No duplicate account was created.
		_db.Users.Count(u => u.Username == "alice").Should().Be(1);
	}

	[Fact]
	public async Task Add_new_user_without_password_shows_visible_error_and_creates_nothing()
	{
		var page = Page();
		var result = await page.OnPostAddAsync(Ws, "bob", Password: "   ", WorkspaceRole.Member);

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().NotBeNullOrEmpty();

		_db.Users.Any(u => u.Username == "bob").Should().BeFalse();
		_db.WorkspaceMembers.Any(m => m.WorkspaceKey == Ws).Should().BeFalse();
	}

	[Fact]
	public async Task Add_new_user_with_password_creates_loginable_account_and_membership()
	{
		var page = Page();
		var result = await page.OnPostAddAsync(Ws, "carol", "s3cret", WorkspaceRole.Admin);

		result.Should().BeOfType<RedirectToPageResult>();
		var user = _db.Users.FirstOrDefault(u => u.Username == "carol");
		user.Should().NotBeNull();
		user!.PasswordHash.Should().NotBeNullOrEmpty();
		_db.WorkspaceMembers.Count(m => m.UserId == user.Id && m.WorkspaceKey == Ws && m.Role == WorkspaceRole.Admin).Should().Be(1);
	}

	// workspace-member-role-edit: changing a member's role in place — no more remove+re-add.
	[Fact]
	public async Task SetRole_changes_a_members_role_in_the_db()
	{
		var userId = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "dave", PasswordHash = ExistingHash, CreatedAt = DateTime.UtcNow });
		await _db.InsertAsync(new WorkspaceMember { UserId = userId, WorkspaceKey = Ws, Role = WorkspaceRole.Member });

		var page = Page();
		var result = await page.OnPostSetRoleAsync(Ws, userId, WorkspaceRole.Viewer);

		result.Should().BeOfType<RedirectToPageResult>();
		_db.WorkspaceMembers.First(m => m.UserId == userId && m.WorkspaceKey == Ws).Role.Should().Be(WorkspaceRole.Viewer);
	}

	// The last-admin guard: demoting the sole admin would leave the workspace with nobody able to
	// administer it (only a sysadmin could recover it) — reject in place, same as Remove below.
	[Fact]
	public async Task SetRole_refuses_to_demote_the_last_admin()
	{
		var userId = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "solo-admin", PasswordHash = ExistingHash, CreatedAt = DateTime.UtcNow });
		await _db.InsertAsync(new WorkspaceMember { UserId = userId, WorkspaceKey = Ws, Role = WorkspaceRole.Admin });

		var page = Page();
		var result = await page.OnPostSetRoleAsync(Ws, userId, WorkspaceRole.Member);

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().NotBeNullOrEmpty();
		_db.WorkspaceMembers.First(m => m.UserId == userId && m.WorkspaceKey == Ws).Role.Should().Be(WorkspaceRole.Admin,
			"the demote must be rejected, not merely warned about");
	}

	// The guard is about the LAST admin, not admin-demotion in general — a second admin remaining
	// makes the demote safe.
	[Fact]
	public async Task SetRole_allows_demoting_an_admin_when_another_admin_remains()
	{
		var admin1 = await _db.InsertWithInt64IdentityAsync(new User { Username = "a1", PasswordHash = ExistingHash, CreatedAt = DateTime.UtcNow });
		var admin2 = await _db.InsertWithInt64IdentityAsync(new User { Username = "a2", PasswordHash = ExistingHash, CreatedAt = DateTime.UtcNow });
		await _db.InsertAsync(new WorkspaceMember { UserId = admin1, WorkspaceKey = Ws, Role = WorkspaceRole.Admin });
		await _db.InsertAsync(new WorkspaceMember { UserId = admin2, WorkspaceKey = Ws, Role = WorkspaceRole.Admin });

		var page = Page();
		var result = await page.OnPostSetRoleAsync(Ws, admin1, WorkspaceRole.Member);

		result.Should().BeOfType<RedirectToPageResult>();
		_db.WorkspaceMembers.First(m => m.UserId == admin1 && m.WorkspaceKey == Ws).Role.Should().Be(WorkspaceRole.Member);
	}

	// Same hole, the other door: Remove used to have NO last-admin guard at all — this is the bug
	// the card called out ("проверь!"). It existed before this change.
	[Fact]
	public async Task Remove_refuses_to_remove_the_last_admin()
	{
		var userId = await _db.InsertWithInt64IdentityAsync(
			new User { Username = "solo-admin2", PasswordHash = ExistingHash, CreatedAt = DateTime.UtcNow });
		await _db.InsertAsync(new WorkspaceMember { UserId = userId, WorkspaceKey = Ws, Role = WorkspaceRole.Admin });

		var page = Page();
		var result = await page.OnPostRemoveAsync(Ws, userId);

		result.Should().BeOfType<PageResult>();
		page.ErrorMessage.Should().NotBeNullOrEmpty();
		_db.WorkspaceMembers.Any(m => m.UserId == userId && m.WorkspaceKey == Ws).Should().BeTrue(
			"the last admin must still be a member — the removal must be rejected");
	}

	[Fact]
	public async Task Remove_allows_removing_an_admin_when_another_admin_remains()
	{
		var admin1 = await _db.InsertWithInt64IdentityAsync(new User { Username = "b1", PasswordHash = ExistingHash, CreatedAt = DateTime.UtcNow });
		var admin2 = await _db.InsertWithInt64IdentityAsync(new User { Username = "b2", PasswordHash = ExistingHash, CreatedAt = DateTime.UtcNow });
		await _db.InsertAsync(new WorkspaceMember { UserId = admin1, WorkspaceKey = Ws, Role = WorkspaceRole.Admin });
		await _db.InsertAsync(new WorkspaceMember { UserId = admin2, WorkspaceKey = Ws, Role = WorkspaceRole.Admin });

		var page = Page();
		var result = await page.OnPostRemoveAsync(Ws, admin1);

		result.Should().BeOfType<RedirectToPageResult>();
		_db.WorkspaceMembers.Any(m => m.UserId == admin1 && m.WorkspaceKey == Ws).Should().BeFalse();
	}
}
