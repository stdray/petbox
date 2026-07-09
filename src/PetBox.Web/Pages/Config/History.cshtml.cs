using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config.Contract;
using PetBox.Config.Data;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Config;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class HistoryModel : PageModel
{
	readonly IConfigService _configService;

	public HistoryModel(IConfigService configService) => _configService = configService;

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
		Entries = await _configService.GetHistoryAsync(EffectiveWorkspaceKey, PathFilter, ct);
	}

	string ResolveWorkspace()
	{
		if (!string.IsNullOrEmpty(WorkspaceKey))
			return WorkspaceKey;
		var claimWs = User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		return string.IsNullOrEmpty(claimWs) ? "$system" : claimWs;
	}
}
