using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;

namespace PetBox.Web.Pages.Admin;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly PetBoxDb _db;

	public IndexModel(PetBoxDb db) => _db = db;

	public int WorkspaceCount { get; private set; }
	public int ProjectCount { get; private set; }
	public int UserCount { get; private set; }
	public int SettingOverrideCount { get; private set; }

	public void OnGet()
	{
		WorkspaceCount = _db.Workspaces.Count();
		ProjectCount = _db.Projects.Count();
		UserCount = _db.Users.Count();
		// Count of system-wide setting rows (defaults). Per-project/per-user overrides
		// count separately when their pages need it.
		SettingOverrideCount = _db.Settings.Count(s => s.Scope == "System");
	}
}
