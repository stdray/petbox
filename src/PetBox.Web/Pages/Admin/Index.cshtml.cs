using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Deploy.Contract;

namespace PetBox.Web.Pages.Admin;

// /ui/admin/sys shows counters across EVERY workspace, project and user — sysadmin only
// (workspace-admin-gate). A bare [Authorize] exposed it to any signed-in account.
[Authorize(Policy = "SysAdmin")]
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
