using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Deploy.Contract;

namespace PetBox.Web.Pages.Admin;

[Authorize]
public sealed class IndexModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;
	readonly IDeployService _deploy;

	public IndexModel(PetBoxDb db, FeatureFlags features, IDeployService deploy)
	{
		_db = db;
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
		WorkspaceCount = await _db.Workspaces.CountAsync(ct);
		ProjectCount = await _db.Projects.CountAsync(ct);
		UserCount = await _db.Users.CountAsync(ct);
		// Count of system-wide setting rows (defaults). Per-project/per-user overrides
		// count separately when their pages need it.
		SettingOverrideCount = await _db.Settings.CountAsync(s => s.Scope == "System", ct);
		// All DB-minted API keys — the Agent keys overview counts the same rows it lists.
		AgentKeyCount = await _db.ApiKeys.CountAsync(ct);

		if (DeployEnabled)
			DeployNodeCount = (await _deploy.ListNodesAsync(ct)).Count;
	}
}
