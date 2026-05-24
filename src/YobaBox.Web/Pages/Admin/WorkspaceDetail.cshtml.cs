using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class WorkspaceDetailModel : PageModel
{
	readonly YobaBoxDb _db;

	public WorkspaceDetailModel(YobaBoxDb db) => _db = db;

	public Workspace? Workspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];

	public void OnGet(string key)
	{
		Workspace = _db.Workspaces.FirstOrDefault(w => w.Key == key);
		if (Workspace is not null)
			Projects = _db.Projects.Where(p => p.WorkspaceKey == key).OrderBy(p => p.Key).ToList();
	}
}
