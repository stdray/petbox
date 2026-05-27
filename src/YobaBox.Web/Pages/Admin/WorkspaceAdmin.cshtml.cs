using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Config.Data;
using YobaBox.Core.Data;
using YobaBox.Core.Models;

namespace YobaBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceMember")]
public sealed class WorkspaceAdminModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly IConfigDbFactory _configFactory;

	public WorkspaceAdminModel(YobaBoxDb db, IConfigDbFactory configFactory)
	{
		_db = db;
		_configFactory = configFactory;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	public Workspace? Workspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public int MemberCount { get; private set; }
	public int ProjectCount { get; private set; }
	public int BindingCount { get; private set; }

	public void OnGet()
	{
		Workspace = _db.Workspaces.FirstOrDefault(w => w.Key == WorkspaceKey);
		if (Workspace is null) return;

		Projects = _db.Projects.Where(p => p.WorkspaceKey == WorkspaceKey).OrderBy(p => p.Key).ToList();
		ProjectCount = Projects.Count;
		MemberCount = _db.WorkspaceMembers.Count(m => m.WorkspaceKey == WorkspaceKey);

		var configDb = _configFactory.GetConfigDb(WorkspaceKey);
		BindingCount = configDb.Bindings.Count(b => !b.IsDeleted);
	}
}
