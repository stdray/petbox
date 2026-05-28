using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceViewer")]
public sealed class WorkspaceDetailModel : PageModel
{
	readonly PetBoxDb _db;

	public WorkspaceDetailModel(PetBoxDb db) => _db = db;

	public Workspace? Workspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public int MemberCount { get; private set; }

	public void OnGet(string key)
	{
		Workspace = _db.Workspaces.FirstOrDefault(w => w.Key == key);
		if (Workspace is not null)
		{
			Projects = _db.Projects.Where(p => p.WorkspaceKey == key).OrderBy(p => p.Key).ToList();
			MemberCount = _db.WorkspaceMembers.Count(m => m.WorkspaceKey == key);
		}
	}
}
