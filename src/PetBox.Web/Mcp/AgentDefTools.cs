using System.ComponentModel;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Services;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for portable agent-definition documents (agent-definition-as-data).
// Project-scoped named temporal docs in the Core DB. Scopes: agents:read / agents:write.
// Tools throw on a failed Assert*; McpErrorEnvelopeFilter renders the {error} body.
// TENANT DECLARATION (spec authz-scope-declaration): the target tenant is the `projectKey` ARGUMENT,
// on all four verbs — hence one declaration on the TYPE rather than four identical ones on the
// methods, which is how families end up different by accident.
//
// This family goes early in the rollout because its manual coverage was already COMPLETE: every one
// of the four called ModuleMcp.AssertProject(http, projectKey) as its first act, against the very
// same ProjectScope.EvaluateAsync that ITenantAuthorizer now runs. So enforcement here changes the
// allow/deny outcome on nothing at all — only WHERE the refusal happens (in the filter, before the
// tool body and before McpProjectExistsFilter) and what it SAYS. The four AssertProject calls are
// deleted in this same commit: a check kept "just in case" behind a live PEP is a second source of
// truth, and the day the two disagree the wrong one wins silently.
[McpServerToolType]
[TenantFrom(TenantSource.Argument, "projectKey")]
public static class AgentDefTools
{
	[McpServerTool(Name = "agent_def_list", Title = "List agent definitions", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentDefListResult))]
	[Description("List portable agent-definition documents for a project (compact rows: key/name/version/updated). Full document via agent_def_get. Requires agents:read.")]
	public static async Task<AgentDefListResult> ListAsync(
		IHttpContextAccessor http, IAgentDefinitionService svc,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AgentsRead);
		var items = await svc.ListAsync(projectKey, ct);
		return new AgentDefListResult(
			items.Select(i => new AgentDefListItemView(i.Key, i.Name, i.Version, i.Updated)).ToList());
	}

	[McpServerTool(Name = "agent_def_get", Title = "Get an agent definition", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentDefGetResult))]
	[Description("Return ONE portable agent-definition document by key (slug). An addressed read: a key matching no definition is a clear ERROR naming the key and the project, not a `found:false` success — the same contract as tasks_node_get and the methodology get verbs. Use agent_def_list to see what exists. Document carries roles with tier/requiredCapabilities/spawn/escalation/notes — NO model fields (model binding is local). Requires agents:read.")]
	public static async Task<AgentDefGetResult> GetAsync(
		IHttpContextAccessor http, IAgentDefinitionService svc,
		string projectKey,
		[Description("Definition slug key (e.g. default).")] string key,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AgentsRead);
		var view = await svc.GetAsync(projectKey, key, ct);
		// ONE not-found contract on this surface (mcp-surface-naming-cleanup wave 5). This verb was
		// the last holdout answering a miss with found:false while every sibling addressed read threw,
		// and the field it set was the only reason `found` existed on the result at all. A caller that
		// forgets to test it reads a null-everything document as a real one — the failure mode the
		// error-shaped contract removes outright.
		if (view is null)
			throw new ArgumentException($"agent definition '{key}' not found in project '{projectKey}' (agent_def_list shows what is stored)");
		return new AgentDefGetResult(
			Key: view.Key,
			Name: view.Definition.Name,
			Roles: view.Definition.Roles.Select(MapRole).ToList(),
			Version: view.Version,
			Created: view.Created,
			Updated: view.Updated);
	}

	[McpServerTool(Name = "agent_def_upsert", Title = "Upsert an agent definition", UseStructuredContent = true, OutputSchemaType = typeof(AgentDefUpsertResult))]
	[Description("""
		Write roles into a portable agent-definition document (tier/requiredCapabilities/spawn/
		escalation/notes). Does NOT carry model binding — role.model is rejected.
		`definition` is ONE typed object in the SAME shape agent_def_get returns —
		{ name?, roles:[{ slug, tier?, requiredCapabilities?, spawn?:{allowed?,allowedRoles?},
		escalation?:{available?,targets?}, notes?, deleted? }] } — so a read can be edited and pasted
		straight back with no reshaping.
		MERGE BY ROLE, not a document replace: `definition.roles` carries only the roles you are
		CHANGING. A role you do NOT send is left exactly as it is, and on a role you DO send an
		omitted field stays UNCHANGED; on a NEW role (a slug the document does not have yet) an
		omitted field starts empty, and tier/requiredCapabilities are required so a half-specified
		new role is refused rather than stored. Deleting a role is EXPLICIT:
		`{ slug, deleted:true }` — absence never deletes.
		`requiredCapabilities`/`spawn.allowedRoles`/`escalation.targets`: omit to keep the current
		set, `[]` CLEARS it, a non-empty list REPLACES it. `notes`: omit to keep, "" to clear. Either
		half of `spawn`/`escalation` may be sent alone and the other half is kept; omit the block
		entirely and it is untouched.
		`key` is the definition slug; `definition.name` sets the document name (omit to keep it);
		`version` is the watermark baseline from agent_def_get (0 = create) and covers the WHOLE
		document, so a stale baseline is refused — re-read and resend only your roles.
		A member this schema does not declare is REFUSED naming it, at every depth — never dropped.
		Identical resubmit → changed:false. Returns { key, version, changed }. Requires agents:write.
		""")]
	public static async Task<AgentDefUpsertResult> UpsertAsync(
		IHttpContextAccessor http, IAgentDefinitionService svc,
		string projectKey,
		[Description("Definition slug key (^[a-z][a-z0-9_-]{0,99}$).")] string key,
		[Description("The definition document, SAME shape agent_def_get returns: { name?, roles:[{ slug, tier?, requiredCapabilities?, spawn?, escalation?, notes?, deleted? }] }. `roles` carries ONLY the roles you are changing — a role not listed is untouched.")]
		AgentDefDocumentInput definition,
		[Description("Watermark baseline: version from last agent_def_get; 0 = create.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AgentsWrite);
		// No hand parser any more. The SDK binds `definition` against the PUBLISHED per-field schema
		// under UnmappedMemberHandling.Disallow, so a member the type does not declare — `model`
		// included, whose rejection was the sole stated reason the old JsonElement parse existed — is
		// REFUSED by name at any depth instead of being dropped. AgentDefinitionJson.Parse still
		// re-checks the merged document afterwards, which is what covers the REST path and any
		// unknown property that survived on the stored tree.
		var roles = definition?.Roles;
		var edits = roles is null or { Count: 0 }
			? throw new ArgumentException("'definition.roles': empty batch — nothing to write. Send the role(s) you are changing; a role you omit is left untouched, and deleting one is explicit ({ slug, deleted:true }).")
			: roles.Select(ToEdit).ToList();
		var ack = await svc.MergeRolesAsync(projectKey, key, definition!.Name, edits, version, ct);
		return new AgentDefUpsertResult(ack.Key, ack.Version, ack.Changed);
	}

	// Flatten the nested wire role into the internal edit record. The two spawn/escalation halves
	// travel together from here on because MergeFlagBlock has to weigh them together (see
	// RoleMergeEdit). A block the caller omitted stays null on BOTH halves, which is what makes
	// "the block is not this call's business" expressible.
	static RoleMergeEdit ToEdit(AgentDefRoleInput r) => new(
		Slug: r.Slug ?? "",
		Tier: r.Tier,
		RequiredCapabilities: r.RequiredCapabilities,
		SpawnAllowed: r.Spawn?.Allowed,
		SpawnAllowedRoles: r.Spawn?.AllowedRoles,
		EscalationAvailable: r.Escalation?.Available,
		EscalationTargets: r.Escalation?.Targets,
		Notes: r.Notes,
		Deleted: r.Deleted);

	[McpServerTool(Name = "agent_def_delete", Title = "Delete an agent definition", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentDefDeleteResult))]
	[Description("Delete a portable agent-definition document (temporal soft-close). Missing key is an idempotent no-op (deleted:false). `version` is the watermark baseline from agent_def_get. Requires agents:write.")]
	public static async Task<AgentDefDeleteResult> DeleteAsync(
		IHttpContextAccessor http, IAgentDefinitionService svc,
		string projectKey,
		[Description("Definition slug key to delete.")] string key,
		[Description("Watermark baseline from last agent_def_get; 0 = delete current regardless.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AgentsWrite);
		var ack = await svc.DeleteAsync(projectKey, key, version, ct);
		return new AgentDefDeleteResult(ack.Key, Deleted: ack.Changed, ack.Version);
	}

	static AgentDefRoleView MapRole(AgentDefinitionRole r) => new(
		r.Slug,
		r.Tier,
		r.RequiredCapabilities,
		r.Spawn is null ? null : new AgentDefSpawnView(r.Spawn.Allowed, r.Spawn.AllowedRoles),
		r.Escalation is null ? null : new AgentDefEscalationView(r.Escalation.Available, r.Escalation.Targets),
		r.Notes);
}
