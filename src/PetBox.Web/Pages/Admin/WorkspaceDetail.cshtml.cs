using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceViewer")]
public sealed class WorkspaceDetailModel : PageModel
{
	readonly ICoreDbFactory _f;

	public WorkspaceDetailModel(ICoreDbFactory f) => _f = f;

	public Workspace? Workspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public int MemberCount { get; private set; }

	public void OnGet(string key)
	{
		using var db = _f.Open();
		Workspace = db.Workspaces.FirstOrDefault(w => w.Key == key);
		if (Workspace is not null)
		{
			Projects = db.Projects.Where(p => p.WorkspaceKey == key).OrderBy(p => p.Key).ToList();
			MemberCount = db.WorkspaceMembers.Count(m => m.WorkspaceKey == key);
		}
	}
}
