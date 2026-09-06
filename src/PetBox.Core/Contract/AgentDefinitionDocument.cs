using System.Text.Json;

namespace PetBox.Core.Contract;

// THE PORTABLE AGENT-DEFINITION DOCUMENT — the typed shape of src/common/default-agents.json, and
// nothing else.
//
// WHAT THIS FILE IS *NOW*. It used to be `AgentDefinitionViews.cs`: the wire/view/edit contract of a
// server-side agent-definition STORE (REST `/api/{p}/agent-defs`, the `agent_def_*` MCP family, the
// admin editor, a temporal SQLite table). That whole surface is gone — work
// agent-defs-server-teardown: the wiring kit builds its definition from files on disk and never asks
// the server for one, so a second authoritative copy per project was a source of drift with no
// reader. Deleted with it: the Ack/View/ListItem views, the RoleFormEdit/RoleMergeEdit edit records,
// and the JsonNode surgery (CanonicalizeRaw / PatchRole / AddRole / RemoveRole / MergeRoles) that
// existed only so the admin form and `agent_def_upsert` could edit a stored document in place.
//
// WHAT SURVIVES, AND WHY. The parse + validate path, because it is a RATCHET: `default-agents.json`
// still ships (embedded here for the server, copied into the npm package for the kit — see
// src/common/README.md), and a malformed or self-inconsistent baseline must go red in CI rather than
// on a user's machine at wire time. DefaultAgentDefinition is the only production reader left; it
// loads the embedded file through Parse below, so the checks run on every build.
//
// JSON wire: camelCase. Unknown properties are ignored for forward-compat EXCEPT any property named
// "model" anywhere in the tree (root, roles, spawn, nested), which is REJECTED — model binding is
// local (~/.petbox/roles.json), never part of a portable definition.

public sealed record AgentDefinitionSpawn(
	bool Allowed,
	IReadOnlyList<string>? AllowedRoles = null);

public sealed record AgentDefinitionEscalation(
	bool Available,
	IReadOnlyList<string>? Targets = null);

// Notes: free-text prose for the role (e.g. a worker's "you are a LEAF, never spawn subagents…"
// briefing). Optional at THIS level — DefaultAgentDefinition.Validate is what additionally requires
// it of the canonical baseline, where a role without prose is a role that briefs nobody.
public sealed record AgentDefinitionRole(
	string Slug,
	string Tier,
	IReadOnlyList<string> RequiredCapabilities,
	AgentDefinitionSpawn? Spawn = null,
	AgentDefinitionEscalation? Escalation = null,
	string? Notes = null);

public sealed record AgentDefinitionDoc(
	string Name,
	IReadOnlyList<AgentDefinitionRole> Roles);

// Parse + the field-level floor for the document shape above.
public static class AgentDefinitionJson
{
	// camelCase, the shape the kit reads the same file with.
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

	// Parse a definition document from JSON. Rejects any "model" property in the tree (portable
	// roster only). Other unknown properties are ignored (forward-compat).
	public static AgentDefinitionDoc Parse(string json)
	{
		using var doc = JsonDocument.Parse(json);
		RejectModelField(doc.RootElement);
		var def = doc.RootElement.Deserialize<AgentDefinitionDoc>(Options)
			?? throw new ArgumentException("agent definition body is required");
		Validate(def);
		return def;
	}

	// The field-level floor: the same checks the kit's validateAgentDefinition applies. The rules
	// that only make sense for the CANONICAL baseline — unique slugs, prose actually present, spawn
	// and escalation targets resolving inside the same document — live in
	// DefaultAgentDefinition.Validate, which calls this one first.
	public static void Validate(AgentDefinitionDoc def)
	{
		if (string.IsNullOrWhiteSpace(def.Name))
			throw new ArgumentException("definition.name is required");
		if (def.Roles is null || def.Roles.Count == 0)
			throw new ArgumentException("definition.roles must contain at least one role");
		foreach (var role in def.Roles)
		{
			if (string.IsNullOrWhiteSpace(role.Slug))
				throw new ArgumentException("each role.slug is required");
			if (string.IsNullOrWhiteSpace(role.Tier))
				throw new ArgumentException($"role '{role.Slug}': tier is required");
			if (role.RequiredCapabilities is null)
				throw new ArgumentException($"role '{role.Slug}': requiredCapabilities is required (may be empty)");
		}
	}

	// Portable definitions MUST NOT carry model binding — that axis is local.
	// Walk the entire JSON tree and reject ANY property named "model" (root, role,
	// spawn, escalation, nested objects/arrays).
	static void RejectModelField(JsonElement el) => RejectModelField(el, path: "$");

	static void RejectModelField(JsonElement el, string path)
	{
		switch (el.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var prop in el.EnumerateObject())
				{
					if (prop.NameEquals("model"))
						throw new ArgumentException(
							$"property 'model' is not allowed on portable agent definitions (at {path}.model) — model binding is local, not part of the definition document");
					RejectModelField(prop.Value, $"{path}.{prop.Name}");
				}
				break;
			case JsonValueKind.Array:
				var i = 0;
				foreach (var item in el.EnumerateArray())
				{
					RejectModelField(item, $"{path}[{i}]");
					i++;
				}
				break;
		}
	}
}
