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

	WorkspaceUsersModel Page() => new(_db);

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
}
