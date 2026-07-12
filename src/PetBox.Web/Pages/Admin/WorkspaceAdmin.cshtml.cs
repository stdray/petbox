using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config.Data;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceMember")]
public sealed class WorkspaceAdminModel : PageModel
{
	readonly ICoreDbFactory _f;
	readonly IConfigDbFactory _configFactory;

	public WorkspaceAdminModel(ICoreDbFactory f, IConfigDbFactory configFactory)
	{
		_f = f;
		_configFactory = configFactory;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	public Workspace? Workspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public int MemberCount { get; private set; }
	public int ProjectCount { get; private set; }
	public int BindingCount { get; private set; }

	public void OnGet()
	{
		using var db = _f.Open();
		Workspace = db.Workspaces.FirstOrDefault(w => w.Key == WorkspaceKey);
		if (Workspace is null) return;

		Projects = db.Projects.Where(p => p.WorkspaceKey == WorkspaceKey).OrderBy(p => p.Key).ToList();
		ProjectCount = Projects.Count;
		MemberCount = db.WorkspaceMembers.Count(m => m.WorkspaceKey == WorkspaceKey);

		using var configDb = _configFactory.NewConfigDb(WorkspaceKey);
		BindingCount = configDb.Bindings.Count(b => !b.IsDeleted);
	}
}
