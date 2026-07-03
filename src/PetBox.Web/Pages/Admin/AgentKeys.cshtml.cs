using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Data;

namespace PetBox.Web.Pages.Admin;

// Sysadmin management view for DB-minted API keys across all projects: lists them (expiring
// and permanent) and revokes. Minting moved off this page — per-project keys are minted from
// a project's Connect page; cross-project / TTL keys via the MCP apikey_create tool.
[Authorize(Policy = "SysAdmin")]
public sealed class AgentKeysModel : PageModel
{
	readonly PetBoxDb _db;

	public AgentKeysModel(PetBoxDb db) => _db = db;

	public sealed record KeyRow(string Key, string Name, string ProjectKey, string Scopes, DateTime CreatedAt, DateTime? ExpiresAt, bool Expired);

	public IReadOnlyList<KeyRow> Keys { get; private set; } = [];

	public void OnGet() => Load();

	void Load()
	{
		// All DB-minted keys, expiring and permanent — the sysadmin overview. Config-declared
		// keys (appsettings/env) are not rows and don't appear here.
		var now = DateTime.UtcNow;
		Keys = [.. _db.ApiKeys
			.OrderByDescending(k => k.CreatedAt)
			.ToList()
			.Select(k => new KeyRow(k.Key, k.Name, k.ProjectKey, k.Scopes, k.CreatedAt, k.ExpiresAt, k.ExpiresAt != null && k.ExpiresAt <= now))];
	}

	public async Task<IActionResult> OnPostRevokeAsync(string key)
	{
		await _db.ApiKeys.Where(k => k.Key == key).DeleteAsync();
		return RedirectToPage();
	}
}
