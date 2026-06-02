using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

// Sysadmin UI for temporary agent/onboarding keys (Phase 27.1). Lists DB-minted keys that
// carry an expiry, lets you issue a new one (scopes + TTL) and revoke. Keys with
// admin:provision can drive the provisioning MCP tools.
[Authorize(Policy = "SysAdmin")]
public sealed class AgentKeysModel : PageModel
{
	readonly PetBoxDb _db;

	public AgentKeysModel(PetBoxDb db) => _db = db;

	public sealed record KeyRow(string Key, string Name, string ProjectKey, string Scopes, DateTime CreatedAt, DateTime? ExpiresAt, bool Expired);

	public IReadOnlyList<KeyRow> Keys { get; private set; } = [];
	public IReadOnlyList<ApiKeyScope> AllScopes => ApiKeyScopes.All;
	public string? ErrorMessage { get; set; }
	public string? IssuedKey { get; set; }

	public void OnGet() => Load();

	void Load()
	{
		var now = DateTime.UtcNow;
		Keys = [.. _db.ApiKeys
			.Where(k => k.ExpiresAt != null)
			.OrderByDescending(k => k.CreatedAt)
			.ToList()
			.Select(k => new KeyRow(k.Key, k.Name, k.ProjectKey, k.Scopes, k.CreatedAt, k.ExpiresAt, k.ExpiresAt <= now))];
	}

	public async Task<IActionResult> OnPostIssueAsync(string? name, string? projectKey, string[]? scopes, int ttlHours, bool allProjects = false)
	{
		var (valid, invalid) = ApiKeyScopes.Validate(scopes is null ? null : string.Join(',', scopes));
		if (invalid.Count > 0)
		{
			ErrorMessage = $"Unknown scopes: {string.Join(", ", invalid)}";
			Load();
			return Page();
		}
		if (valid.Count == 0)
		{
			ErrorMessage = "Select at least one scope.";
			Load();
			return Page();
		}
		if (ttlHours <= 0)
		{
			ErrorMessage = "TTL must be a positive number of hours.";
			Load();
			return Page();
		}

		var keyValue = $"yb_key_{Guid.NewGuid():N}";
		await _db.InsertAsync(new ApiKey
		{
			Key = keyValue,
			// allProjects mints a CROSS-PROJECT key (project="*") that reads+writes across every
			// project — for monitoring/dev override. Otherwise scoped to one project ($system default).
			ProjectKey = allProjects ? ProjectScope.AllProjects
				: string.IsNullOrWhiteSpace(projectKey) ? "$system" : projectKey.Trim(),
			Scopes = string.Join(',', valid),
			Name = string.IsNullOrWhiteSpace(name) ? "agent-key" : name.Trim(),
			CreatedAt = DateTime.UtcNow,
			ExpiresAt = DateTime.UtcNow.AddHours(ttlHours),
		});

		IssuedKey = keyValue;
		Load();
		return Page();
	}

	public async Task<IActionResult> OnPostRevokeAsync(string key)
	{
		await _db.ApiKeys.Where(k => k.Key == key).DeleteAsync();
		return RedirectToPage();
	}
}
