using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using YobaBox.Core.Data;
using YobaBox.Core.Models;
using YobaBox.Log.Core.Retention;
using LinqToDB.Async;

namespace YobaBox.Web.Pages.Admin;

[Authorize]
public sealed class RetentionModel : PageModel
{
	readonly YobaBoxDb _db;
	readonly RetentionOptions _options;

	public RetentionModel(YobaBoxDb db, IOptions<RetentionOptions> options)
	{
		_db = db;
		_options = options.Value;
	}

	public int DefaultRetainDays => _options.DefaultRetainDays;
	public int SystemRetainDays => _options.SystemRetainDays;

	public IReadOnlyList<(Project Project, int? PolicyDays)> Rows { get; private set; } = [];
	public string? ErrorMessage { get; set; }

	public void OnGet()
	{
		Load();
	}

	public async Task<IActionResult> OnPostSetAsync(string projectKey, int retainDays)
	{
		if (string.IsNullOrWhiteSpace(projectKey))
		{
			ErrorMessage = "Project key is required.";
			Load();
			return Page();
		}
		if (retainDays < 1)
		{
			ErrorMessage = "RetainDays must be ≥ 1.";
			Load();
			return Page();
		}

		var now = DateTime.UtcNow;
		var existing = await _db.RetentionPolicies.FirstOrDefaultAsync((RetentionPolicy r) => r.ProjectKey == projectKey);
		if (existing is null)
		{
			await _db.InsertAsync(new RetentionPolicy
			{
				ProjectKey = projectKey,
				RetainDays = retainDays,
				CreatedAt = now,
				UpdatedAt = now,
			});
		}
		else
		{
			await _db.UpdateAsync(existing with { RetainDays = retainDays, UpdatedAt = now });
		}
		return RedirectToPage();
	}

	public async Task<IActionResult> OnPostClearAsync(string projectKey)
	{
		await _db.RetentionPolicies.Where(r => r.ProjectKey == projectKey).DeleteAsync();
		return RedirectToPage();
	}

	void Load()
	{
		var projects = _db.Projects.OrderBy(p => p.Key).ToList();
		var policies = _db.RetentionPolicies.ToList()
			.ToDictionary(p => p.ProjectKey, p => p.RetainDays, StringComparer.Ordinal);
		Rows = projects
			.Select(p => (p, policies.TryGetValue(p.Key, out var d) ? (int?)d : null))
			.ToList();
	}
}
