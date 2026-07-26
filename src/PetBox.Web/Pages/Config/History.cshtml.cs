using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config;
using PetBox.Config.Data;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
// {workspaceKey}, not {projectKey} — read Config/Index.cshtml.cs before changing this. This page is
// mapped by TWO templates (Program.cs AddPageRoute), one workspace-scoped and one project-scoped, and
// a PageModel declares once for both: naming `projectKey` would resolve to nothing on the
// workspace-only template and 403 it. A project-claimed key still reaches this page, because
// ITenantAuthorizer knows a project claim authorizes its own workspace.
[TenantFrom(TenantSource.Route, "workspaceKey", tenant: TenantKind.Workspace)]
public sealed class HistoryModel : PageModel
{
	readonly IConfigDirectory _config;

	public HistoryModel(IConfigDirectory config) => _config = config;

	// authz-bypass-project-create: route-only bind — see Admin/Projects.cshtml.cs for why.
	[FromRoute(Name = "workspaceKey")]
	public string? WorkspaceKey { get; set; }

	[BindProperty(SupportsGet = true, Name = "path")]
	public string? PathFilter { get; set; }

	public string EffectiveWorkspaceKey { get; private set; } = "$system";
	public IReadOnlyList<ConfigBindingHistoryEntry> Entries { get; private set; } = [];

	public async Task OnGetAsync(CancellationToken ct)
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		Entries = await _config.ListHistoryAsync(EffectiveWorkspaceKey, PathFilter, ct: ct);
	}

	// THE TENANT THE PEP JUDGED, and nothing else. Both route templates of this page carry
	// {workspaceKey}, so it is always bound — and [TenantFrom(Route, "workspaceKey", …)] on the class
	// refuses the request when it is not, which is what finally makes that guarantee enforced rather
	// than assumed.
	//
	// The old body fell back to the ActiveWorkspace CLAIM and then to a hard-coded "$system". That
	// fallback was unreachable through routing, but it was also the one way this page could read and
	// WRITE config for a workspace TenantEnforcementMiddleware never saw — the target the decision point
	// judged and the target the handler acts on have to be the same string, so the fallback is deleted
	// rather than left as a comfort. If WorkspaceKey were ever empty here, the request would already
	// have been refused above.
	string ResolveWorkspace() => WorkspaceKey!;
}
