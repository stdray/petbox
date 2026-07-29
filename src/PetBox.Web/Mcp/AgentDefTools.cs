using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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
		Store a portable agent-definition document (roles/tier/requiredCapabilities/spawn/
		escalation/notes). Does NOT carry model binding — role.model is rejected. `key` is the
		definition slug; `version` is the watermark baseline from agent_def_get (0 = create).
		Full-document REPLACE, not a per-field patch: `definition` OVERWRITES the whole stored
		document — a role or field present in the OLD document but ABSENT from THIS call's
		`definition` is GONE, not kept (there is no merge, unlike memory_upsert/tasks_upsert).
		Read via agent_def_get, edit, then resubmit the COMPLETE document.
		Identical resubmit → changed:false. Returns { key, version, changed }. Requires agents:write.
		""")]
	public static async Task<AgentDefUpsertResult> UpsertAsync(
		IHttpContextAccessor http, IAgentDefinitionService svc,
		string projectKey,
		[Description("Definition slug key (^[a-z][a-z0-9_-]{0,99}$).")] string key,
		[McpJsonShape("object")]
		[Description("The portable definition document: { name, roles:[{ slug, tier, requiredCapabilities, spawn?, escalation?, notes? }] }.")] JsonElement definition,
		[Description("Watermark baseline: version from last agent_def_get; 0 = create.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertScope(http, ApiKeyScopes.AgentsWrite);
		// Parse from JsonElement so role.model is rejected on the wire shape.
		var def = ParseDefinition(definition);
		var ack = await svc.UpsertAsync(projectKey, key, def, version, ct);
		return new AgentDefUpsertResult(ack.Key, ack.Version, ack.Changed);
	}

	// Parse the `definition` argument, tolerating the double-encoded form.
	//
	// The schema now declares `definition` as an object ([McpJsonShape]) — before that it was a
	// typeless node and strict clients sent the document as a JSON *string*, which blew up as a raw
	// `JsonException: The JSON value could not be converted to AgentDefinitionDoc. Path: $` (intake
	// mcp-agent-def-upsert-definition-param-untyped). Clients that still double-encode (a stale
	// cached schema, a hand-rolled caller) are accepted: a string is parsed first. Everything that
	// is still not an object fails as a STRUCTURAL ArgumentException — the {error} envelope then
	// carries a message a caller can act on, not a .NET stack trace about `Path: $`.
	static AgentDefinitionDoc ParseDefinition(JsonElement definition)
	{
		switch (definition.ValueKind)
		{
			case JsonValueKind.Object:
				return AgentDefinitionJson.Parse(definition);

			case JsonValueKind.String:
				var raw = definition.GetString() ?? "";
				JsonDocument doc;
				try
				{
					doc = JsonDocument.Parse(raw);
				}
				catch (JsonException ex)
				{
					throw new ArgumentException(
						$"definition must be a JSON object {{ name, roles:[…] }} — got a string that is not valid JSON: {ex.Message}");
				}
				using (doc)
				{
					if (doc.RootElement.ValueKind != JsonValueKind.Object)
						throw new ArgumentException(
							$"definition must be a JSON object {{ name, roles:[…] }} — the string decoded to a JSON {doc.RootElement.ValueKind.ToString().ToLowerInvariant()}");
					return AgentDefinitionJson.Parse(doc.RootElement);
				}

			case JsonValueKind.Undefined:
			case JsonValueKind.Null:
				throw new ArgumentException("definition is required: a JSON object { name, roles:[…] }");

			default:
				throw new ArgumentException(
					$"definition must be a JSON object {{ name, roles:[…] }} — got a JSON {definition.ValueKind.ToString().ToLowerInvariant()}");
		}
	}

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
