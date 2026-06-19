using System.ComponentModel;
using System.Text.RegularExpressions;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// Typed per-type project provisioning tools (typed-surface Phase 4 — replaces the generic
// entity.* type "project"). Flat, typed params give the MCP client a real per-field schema.
// Provisioning ops: admin:provision scope, NO per-project claim — these are cross-project
// onboarding ops. project has no delete (entity.* refused it too: would orphan logs/dbs/keys).
// Tools throw on a failed Assert*/validation; McpErrorEnvelopeFilter renders the {error} body.
[McpServerToolType]
public static partial class ProjectTools
{
	// Project keys: starts a-z, then a-z/0-9/_/- up to 100 chars (same spec as the old entity.*).
	[GeneratedRegex("^[a-z][a-z0-9_-]{0,99}$")]
	private static partial Regex KeyRegex();

	[McpServerTool(Name = "project.create", Title = "Create a project", UseStructuredContent = true, OutputSchemaType = typeof(ProjectCreatedResult))]
	[Description("Creates a project in a workspace. Requires admin:provision. `key` must match ^[a-z][a-z0-9_-]{0,99}$; `name` defaults to the key.")]
	public static async Task<ProjectCreatedResult> CreateAsync(
		IHttpContextAccessor http, PetBoxDb db,
		[Description("Workspace the project belongs to.")] string workspaceKey,
		[Description("Project key (^[a-z][a-z0-9_-]{0,99}$).")] string key,
		[Description("Display name (defaults to the key).")] string? name = null,
		[Description("Optional description.")] string? description = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		if (string.IsNullOrWhiteSpace(key) || !KeyRegex().IsMatch(key))
			throw new ArgumentException($"key '{key}' is invalid; must match ^[a-z][a-z0-9_-]{{0,99}}$");

		if (!await db.Workspaces.AnyAsync((Workspace w) => w.Key == workspaceKey, ct))
			throw new InvalidOperationException($"Workspace '{workspaceKey}' not found");
		if (await db.Projects.AnyAsync((Project p) => p.Key == key, ct))
			throw new InvalidOperationException($"Project '{key}' already exists");

		await db.InsertAsync(new Project
		{
			Key = key,
			WorkspaceKey = workspaceKey,
			Name = string.IsNullOrWhiteSpace(name) ? key : name!,
			Description = description ?? string.Empty,
		}, token: ct);
		return new ProjectCreatedResult(key, workspaceKey, name, description);
	}

	[McpServerTool(Name = "project.list", Title = "List projects", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ProjectListResult))]
	[Description("Lists projects, optionally scoped to one workspace. Requires admin:provision.")]
	public static async Task<ProjectListResult> ListAsync(
		IHttpContextAccessor http, PetBoxDb db,
		[Description("Restrict to one workspace; omit for all projects.")] string? workspaceKey = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		var q = db.Projects.AsQueryable();
		if (!string.IsNullOrEmpty(workspaceKey))
			q = q.Where(p => p.WorkspaceKey == workspaceKey);
		var rows = await q.OrderBy(p => p.Key)
			.Select(p => new ProjectRow(p.Key, p.WorkspaceKey, p.Name, p.Description))
			.ToListAsync(ct);
		return new ProjectListResult(rows);
	}
}
