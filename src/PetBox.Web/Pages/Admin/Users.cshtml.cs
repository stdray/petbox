using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize]
public sealed class UsersModel : PageModel
{
	readonly PetBoxDb _db;

	public UsersModel(PetBoxDb db) => _db = db;

	public sealed record UserRow(long Id, string Username, DateTime CreatedAt, IReadOnlyList<MembershipRow> Memberships);
	public sealed record MembershipRow(string WorkspaceKey, WorkspaceRole Role);

	public IReadOnlyList<UserRow> Users { get; private set; } = [];

	public void OnGet()
	{
		var users = _db.Users.OrderBy(u => u.Username).ToList();
		var members = _db.WorkspaceMembers.ToList();

		Users = [.. users.Select(u => new UserRow(
			u.Id,
			u.Username,
			u.CreatedAt,
			[.. members
				.Where(m => m.UserId == u.Id)
				.OrderBy(m => m.WorkspaceKey)
				.Select(m => new MembershipRow(m.WorkspaceKey, m.Role))]))];
	}
}
