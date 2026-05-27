using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly YobaBoxDb _db;

	public IndexModel(YobaBoxDb db) => _db = db;

	public int WorkspaceCount { get; private set; }
	public int ProjectCount { get; private set; }
	public int UserCount { get; private set; }
	public int RetentionOverrideCount { get; private set; }

	public void OnGet()
	{
		WorkspaceCount = _db.Workspaces.Count();
		ProjectCount = _db.Projects.Count();
		UserCount = _db.Users.Count();
		RetentionOverrideCount = _db.RetentionPolicies.Count();
	}
}
