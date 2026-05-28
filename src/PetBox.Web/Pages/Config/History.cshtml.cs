using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Config.Data;
using PetBox.Core.Auth;

namespace PetBox.Web.Pages.Config;

[Authorize]
public sealed class HistoryModel : PageModel
{
	readonly IConfigDbFactory _configFactory;

	public HistoryModel(IConfigDbFactory configFactory) => _configFactory = configFactory;

	[BindProperty(SupportsGet = true)]
	public string? WorkspaceKey { get; set; }

	[BindProperty(SupportsGet = true, Name = "path")]
	public string? PathFilter { get; set; }

	public string EffectiveWorkspaceKey { get; private set; } = "$system";
	public IReadOnlyList<ConfigBindingHistoryEntry> Entries { get; private set; } = [];

	public void OnGet()
	{
		EffectiveWorkspaceKey = ResolveWorkspace();
		var configDb = _configFactory.GetConfigDb(EffectiveWorkspaceKey);

		var query = configDb.History.AsQueryable();
		if (!string.IsNullOrWhiteSpace(PathFilter))
		{
			var p = PathFilter;
			query = query.Where(h => h.Path.Contains(p));
		}
		Entries = query.OrderByDescending(h => h.At).Take(500).ToList();
	}

	string ResolveWorkspace()
	{
		if (!string.IsNullOrEmpty(WorkspaceKey))
			return WorkspaceKey;
		var claimWs = User.FindFirst(PetBoxClaims.ActiveWorkspace)?.Value;
		return string.IsNullOrEmpty(claimWs) ? "$system" : claimWs;
	}
}
