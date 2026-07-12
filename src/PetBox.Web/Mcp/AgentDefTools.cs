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
[McpServerToolType]
public static class AgentDefTools
{
	[McpServerTool(Name = "agent_def_list", Title = "List agent definitions", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentDefListResult))]
	[Description("List portable agent-definition documents for a project (compact rows: key/name/version/updated). Full document via agent_def_get. Requires agents:read.")]
	public static async Task<AgentDefListResult> ListAsync(
		IHttpContextAccessor http, IAgentDefinitionService svc,
		string projectKey, CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.AgentsRead);
		var items = await svc.ListAsync(projectKey, ct);
		return new AgentDefListResult(
			items.Select(i => new AgentDefListItemView(i.Key, i.Name, i.Version, i.Updated)).ToList());
	}

	[McpServerTool(Name = "agent_def_get", Title = "Get an agent definition", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(AgentDefGetResult))]
	[Description("Return ONE portable agent-definition document by key (slug). Found=false on miss (not an error). Document carries roles with tier/requiredCapabilities/spawn/escalation/notes — NO model fields (model binding is local). Requires agents:read.")]
	public static async Task<AgentDefGetResult> GetAsync(
		IHttpContextAccessor http, IAgentDefinitionService svc,
		string projectKey,
		[Description("Definition slug key (e.g. default).")] string key,
		CancellationToken ct = default)
	{
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.AgentsRead);
		var view = await svc.GetAsync(projectKey, key, ct);
		if (view is null)
			return new AgentDefGetResult(Found: false, Key: key);
		return new AgentDefGetResult(
			Found: true,
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
		await ModuleMcp.AssertProject(http, projectKey, ct);
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
		await ModuleMcp.AssertProject(http, projectKey, ct);
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
