using LinqToDB;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;
using PetBox.Core.Models;

namespace PetBox.Web.Pages.Admin;

// Onboarding helper: mint a project-scoped API key and show copy-paste config to
// wire a coding agent's MCP client to this PetBox instance. The key is shown
// exactly once (only the value lives in the response; only the row is persisted).
// Boards/stores/sessions are then created by the agent via the MCP tools.
[Authorize(Policy = "WorkspaceAdmin")]
public sealed class ProjectConnectModel : PageModel
{
	readonly PetBoxDb _db;
	readonly FeatureFlags _features;

	public ProjectConnectModel(PetBoxDb db, FeatureFlags features)
	{
		_db = db;
		_features = features;
	}

	// Scopes pre-checked for an agent that uses Tasks/Memory/Sessions. Sessions
	// ride on the tasks:* scopes (no dedicated sessions scope) — see SessionTools.
	public static readonly IReadOnlyList<string> AgentDefaultScopes =
	[
		ApiKeyScopes.TasksRead, ApiKeyScopes.TasksWrite,
		ApiKeyScopes.MemoryRead, ApiKeyScopes.MemoryWrite,
	];

	[BindProperty(SupportsGet = true)]
	public string WorkspaceKey { get; set; } = string.Empty;

	[BindProperty(SupportsGet = true)]
	public string ProjectKey { get; set; } = string.Empty;

	public Project? Project { get; private set; }
	public bool ProjectNotFound { get; private set; }
	public string? ErrorMessage { get; private set; }

	// Set only on the POST response right after minting — shown once.
	public string? NewKey { get; private set; }

	public IReadOnlyList<ApiKeyScope> AllScopes => ApiKeyScopes.All;

	// Absolute MCP endpoint for this instance, derived from the current request so
	// it works behind a reverse proxy without extra config.
	public string McpUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/mcp";

	// Public, key-free setup guide (tree model, per-agent config, SKILL.md template).
	// The prompt links here instead of duplicating that content.
	public string DocAgentUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}/doc/agent";

	public async Task<IActionResult> OnGetAsync()
	{
		Project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);
		if (Project is null) { ProjectNotFound = true; return Page(); }
		return Page();
	}

	public async Task<IActionResult> OnPostMintAsync(string name, string[]? scopes)
	{
		Project = await _db.Projects.FirstOrDefaultAsync((Project p) => p.Key == ProjectKey);
		if (Project is null) { ProjectNotFound = true; return Page(); }

		if (string.IsNullOrWhiteSpace(name))
		{
			ErrorMessage = "Name is required.";
			return Page();
		}

		var raw = scopes is null ? "" : string.Join(",", scopes);
		var (valid, invalid) = ApiKeyScopes.Validate(raw);
		if (invalid.Count > 0)
		{
			ErrorMessage = "Unknown scope(s): " + string.Join(", ", invalid);
			return Page();
		}
		if (valid.Count == 0)
		{
			ErrorMessage = "Select at least one scope.";
			return Page();
		}

		var keyValue = $"yb_key_{Guid.NewGuid():N}";
		await _db.InsertAsync(new ApiKey
		{
			Key = keyValue,
			ProjectKey = ProjectKey,
			Scopes = string.Join(",", valid),
			Name = name.Trim(),
			CreatedAt = DateTime.UtcNow,
		});

		NewKey = keyValue;
		return Page();
	}
}
