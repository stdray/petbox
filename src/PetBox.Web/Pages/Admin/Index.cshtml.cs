using LinqToDB;
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
	readonly ICoreDbFactory _f;
	readonly FeatureFlags _features;
	readonly IDeployService _deploy;

	public IndexModel(ICoreDbFactory f, FeatureFlags features, IDeployService deploy)
	{
		_f = f;
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
		using var db = _f.Open();
		WorkspaceCount = await db.Workspaces.CountAsync(ct);
		ProjectCount = await db.Projects.CountAsync(ct);
		UserCount = await db.Users.CountAsync(ct);
		// Count of system-wide setting rows (defaults). Per-project/per-user overrides
		// count separately when their pages need it.
		SettingOverrideCount = await db.Settings.CountAsync(s => s.Scope == "System", ct);
		// All DB-minted API keys — the Agent keys overview counts the same rows it lists.
		AgentKeyCount = await db.ApiKeys.CountAsync(ct);

		if (DeployEnabled)
			DeployNodeCount = (await _deploy.ListNodesAsync(ct)).Count;
	}
}
