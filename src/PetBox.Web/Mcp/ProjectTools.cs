using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Web.Auth;
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

	[McpServerTool(Name = "project_create", Title = "Create a project", UseStructuredContent = true, OutputSchemaType = typeof(ProjectCreatedResult))]
	[Description("""
		Creates a project in a workspace. Requires admin:provision. `key` must match
		^[a-z][a-z0-9_-]{0,99}$; `name` defaults to the key. `sandbox:true` marks it a SANDBOX
		project — the containment target for sandbox-only API keys (apikey_create sandboxOnly:true):
		background/smoke jobs run there exactly as they do in production (enrichment stays on), but a
		sandbox-only key can physically write ONLY into projects flagged sandbox here. Use it for
		smoke-test / throwaway traffic that must never land in a real project.
		""")]
	public static async Task<ProjectCreatedResult> CreateAsync(
		IHttpContextAccessor http, IProjectDirectory projects,
		[Description("Workspace the project belongs to.")] string workspaceKey,
		[Description("Project key (^[a-z][a-z0-9_-]{0,99}$).")] string key,
		[Description("Display name (defaults to the key).")] string? name = null,
		[Description("Optional description.")] string? description = null,
		[Description("Marks this a SANDBOX project — the write-gate containment target for sandbox-only API keys. Default false.")] bool sandbox = false,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);
		if (string.IsNullOrWhiteSpace(workspaceKey)) throw new ArgumentException("workspaceKey is required");
		if (string.IsNullOrWhiteSpace(key) || !KeyRegex().IsMatch(key))
			throw new ArgumentException($"key '{key}' is invalid; must match ^[a-z][a-z0-9_-]{{0,99}}$");

		// IProjectDirectory is THE catalog: the workspace-exists / reserved-key / $system / duplicate
		// rules live there, so this tool cannot drift from the admin page that creates projects too.
		var result = await projects.CreateAsync(
			workspaceKey, key, string.IsNullOrWhiteSpace(name) ? key : name!, description, sandbox, ct);

		return result switch
		{
			ProjectChangeResult.Created => new ProjectCreatedResult(key, workspaceKey, name, description, sandbox),
			// NotFound out of a CREATE is the workspace — the project is what we are making.
			ProjectChangeResult.NotFound => throw new InvalidOperationException($"Workspace '{workspaceKey}' not found"),
			ProjectChangeResult.Refused r => throw new InvalidOperationException(r.Reason),
			_ => throw new InvalidOperationException("Project could not be created"),
		};
	}

	[McpServerTool(Name = "project_list", Title = "List projects", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(ProjectListResult))]
	[Description("Lists projects, optionally scoped to one workspace. Requires admin:provision.")]
	public static async Task<ProjectListResult> ListAsync(
		IHttpContextAccessor http, IProjectDirectory projects,
		[Description("Restrict to one workspace; omit for all projects.")] string? workspaceKey = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AdminProvision);

		// includeContainers: this is the ADMIN/provisioning view — the $ws-* memory containers are real
		// Projects rows here, and hiding them from a provisioning agent (which the pages' default does,
		// because a container is not a user project) would change what this tool has always answered.
		var rows = string.IsNullOrEmpty(workspaceKey)
			? await projects.ListAllAsync(includeContainers: true, ct)
			: await projects.ListAsync(workspaceKey, includeContainers: true, ct);

		return new ProjectListResult(
			[.. rows.Select(p => new ProjectRow(p.Key, p.WorkspaceKey, p.Name, p.Description, p.Sandbox))]);
	}
}
