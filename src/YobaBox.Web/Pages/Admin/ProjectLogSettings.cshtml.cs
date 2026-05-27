using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YobaBox.Core.Auth;
using YobaBox.Core.Data;
using YobaBox.Core.Settings;
using YobaBox.Web.Settings;

namespace YobaBox.Web.Pages.Admin;

[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectLogSettingsModel : PageModel
{
	readonly ISettingsResolver _resolver;
	readonly YobaBoxDb _db;

	public ProjectLogSettingsModel(ISettingsResolver resolver, YobaBoxDb db)
	{
		_resolver = resolver;
		_db = db;
	}

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public LogSettings Current { get; private set; } = new();
	public bool ProjectExists { get; private set; }
	public string? SuccessMessage { get; set; }
	public string? ErrorMessage { get; set; }

	public async Task OnGetAsync()
	{
		ProjectExists = _db.Projects.Any(p => p.Key == ProjectKey);
		if (!ProjectExists) return;
		Current = await _resolver.GetAsync<LogSettings>(Scope.Project, ProjectKey);
	}

	public async Task<IActionResult> OnPostSaveAsync()
	{
		ProjectExists = _db.Projects.Any(p => p.Key == ProjectKey);
		if (!ProjectExists)
		{
			ErrorMessage = "Project not found.";
			return Page();
		}

		var old = await _resolver.GetAsync<LogSettings>(Scope.Project, ProjectKey);
		var updated = SettingsFormBinder.BuildFrom(Request.Form, old);

		var userIdRaw = User.FindFirst(YobaBoxClaims.UserId)?.Value;
		long? userId = long.TryParse(userIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;

		await _resolver.SetAsync(Scope.Project, ProjectKey, updated, old, userId);

		Current = updated;
		SuccessMessage = "Log settings saved.";
		return Page();
	}
}
