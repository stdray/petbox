using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Deploy.Contract;

namespace PetBox.Web.Pages.Admin;

// /ui/admin/sys shows counters across EVERY workspace, project and user — sysadmin only
// (workspace-admin-gate). A bare [Authorize] exposed it to any signed-in account.
[Authorize(Policy = "SysAdmin")]
// The whole installation, counted: workspaces, projects, users, deploy nodes. There is no tenant slot
// in /ui/admin/sys and no tenant it could be narrowed to — the page's SUBJECT is the fleet. The
// cross-tenant probe already recorded this and the five pages beside it as denying an outsider, but on
// the ROLE axis (SysAdmin), which is orthogonal and stays exactly where it is.
[TenantExempt(TenantExemption.FleetWide,
	"counters across EVERY workspace, project and user in the installation; the fleet is the subject, "
	+ "so there is no tenant to scope it to")]
public sealed class IndexModel : PageModel
{
	readonly ICoreDbRollupService _rollup;
	readonly FeatureFlags _features;
	readonly IDeployService _deploy;

	public IndexModel(ICoreDbRollupService rollup, FeatureFlags features, IDeployService deploy)
	{
		_rollup = rollup;
		_features = features;
		_deploy = deploy;
	}

	public int WorkspaceCount { get; private set; }
	public int ProjectCount { get; private set; }
	public int UserCount { get; private set; }
	public int SettingOverrideCount { get; private set; }
	public int AgentKeyCount { get; private set; }
	public int DeployNodeCount { get; private set; }
	public bool DeployEnabled => _features.IsEnabled(Feature.Deploy);

	public async Task OnGetAsync(CancellationToken ct)
	{
		var rollup = await _rollup.GetAdminRollupAsync(ct);
		WorkspaceCount = rollup.WorkspaceCount;
		ProjectCount = rollup.ProjectCount;
		UserCount = rollup.UserCount;
		SettingOverrideCount = rollup.SettingOverrideCount;
		AgentKeyCount = rollup.AgentKeyCount;

		if (DeployEnabled)
			DeployNodeCount = (await _deploy.ListNodesAsync(ct)).Count;
	}
}
