using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config.Contract;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceMember")]
public sealed class WorkspaceAdminModel : PageModel
{
	readonly PetBoxDb _db;
	readonly IConfigService _configService;

	public WorkspaceAdminModel(PetBoxDb db, IConfigService configService)
	{
		_db = db;
		_configService = configService;
	}

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string WorkspaceKey { get; set; } = string.Empty;

	public Workspace? Workspace { get; private set; }
	public IReadOnlyList<Project> Projects { get; private set; } = [];
	public int MemberCount { get; private set; }
	public int ProjectCount { get; private set; }
	public int BindingCount { get; private set; }

	public async Task OnGetAsync(CancellationToken ct)
	{
		Workspace = await _db.Workspaces.FirstOrDefaultAsync(w => w.Key == WorkspaceKey, ct);
		if (Workspace is null) return;

		Projects = (await _db.Projects.Where(p => p.WorkspaceKey == WorkspaceKey).OrderBy(p => p.Key).ToListAsync(ct))
			.AsReadOnly();
		ProjectCount = Projects.Count;
		MemberCount = await _db.WorkspaceMembers.CountAsync(m => m.WorkspaceKey == WorkspaceKey, ct);

		BindingCount = await _configService.CountBindingsAsync(WorkspaceKey, ct: ct);
	}
}
