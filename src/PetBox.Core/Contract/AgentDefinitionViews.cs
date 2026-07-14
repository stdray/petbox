using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PetBox.Core.Contract;

// Portable agent-definition document (agent-definition-as-data). Server stores the
// roster only — role → model binding and the owner ($HOME) axis are local, not columns.
// JSON wire: camelCase. Unknown properties are ignored for forward-compat EXCEPT
// any property named "model" anywhere in the JSON tree (root, roles, spawn, nested),
// which is rejected (portable definitions must not carry models).

public sealed record AgentDefinitionSpawn(
	bool Allowed,
	IReadOnlyList<string>? AllowedRoles = null);

public sealed record AgentDefinitionEscalation(
	bool Available,
	IReadOnlyList<string>? Targets = null);

// Notes: free-text prose for the role (e.g. a worker's "you are a LEAF, never spawn
// subagents…" briefing). Optional — a role without notes never serializes an empty key
// (DefaultIgnoreCondition = WhenWritingNull on AgentDefinitionJson.Options) and Validate
// never requires it.
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

// Ack of a write/delete: key, current revision, whether this call created a new
// revision (false = identical resubmit / delete no-op). Conflicts throw.
public sealed record AgentDefinitionAck(string Key, long Version, bool Changed);

// Full document + temporal envelope.
public sealed record AgentDefinitionView(
	string Key,
	AgentDefinitionDoc Definition,
	long Version,
	DateTime Created,
	DateTime Updated);

// Compact list row (no full definition body).
public sealed record AgentDefinitionListItem(
	string Key,
	string Name,
	long Version,
	DateTime Updated);

// Structured-form edit of ONE role's known fields — the admin-UI form's payload for
// AgentDefinitionJson.PatchRole. Every list is the FULL replacement set for that field
// (checkboxes, not free text): the caller (page model) is responsible for merging in any
// pre-existing value outside the known capability catalog before it gets here, so a value
// the checkboxes don't offer is never silently dropped.
public sealed record RoleFormEdit(
	string Slug,
	string Tier,
	IReadOnlyList<string> RequiredCapabilities,
	bool SpawnAllowed,
	IReadOnlyList<string> SpawnAllowedRoles,
	bool EscalationAvailable,
	IReadOnlyList<string> EscalationTargets,
	string? Notes);

// Shared JSON options + parse helpers for the agent-definition document wire shape.
public static class AgentDefinitionJson
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	// Parse a definition document from JSON. Rejects any "model" property in the tree
	// (portable roster only). Other unknown properties are ignored (forward-compat).
	public static AgentDefinitionDoc Parse(string json)
	{
		using var doc = JsonDocument.Parse(json);
		return Parse(doc.RootElement);
	}

	public static AgentDefinitionDoc Parse(JsonElement root)
	{
		RejectModelField(root);
		var def = root.Deserialize<AgentDefinitionDoc>(Options)
			?? throw new ArgumentException("agent definition body is required");
		Validate(def);
		return def;
	}

	// Serialize a typed document to the stored/wire JSON form.
	public static string Serialize(AgentDefinitionDoc def) =>
		JsonSerializer.Serialize(def, Options);

	// The canonical STORED form of a raw document: the caller's JSON verbatim — properties
	// outside the typed schema included — with formatting normalized (so an identical resubmit
	// that only differs in whitespace dedupes to a no-op instead of minting a phantom revision).
	// A document carrying no `name` gets the key slug written in: we never store a nameless doc.
	public static string CanonicalizeRaw(string json, string nameFallback)
	{
		var node = JsonNode.Parse(json);
		if (node is not JsonObject obj)
			throw new ArgumentException("agent definition body must be a JSON object");

		if (!obj.TryGetPropertyValue("name", out var name)
			|| name is not JsonValue v
			|| v.GetValueKind() != JsonValueKind.String
			|| string.IsNullOrWhiteSpace(v.GetValue<string>()))
		{
			obj["name"] = nameFallback;
		}

		return obj.ToJsonString(Options);
	}

	// FORM-MODE round trip (agent-def-ui-is-a-json-textarea): patch ONE role's known fields
	// on the document AS STORED, touching nothing else — not other roles, not properties
	// outside the schema (on the root, on this role, or inside its spawn/escalation), not
	// even a field on THIS role whose posted value is identical to what's already there.
	// An edit-nothing save must reproduce the exact same canonical bytes: the raw-JSON
	// textarea already promises that round trip, and the form owes it too.
	public static string PatchRole(string rawJson, int roleIndex, RoleFormEdit edit)
	{
		var root = ParseRoot(rawJson);
		var role = RoleAt(root, roleIndex);

		SetStringIfChanged(role, "slug", edit.Slug);
		SetStringIfChanged(role, "tier", edit.Tier);
		SetRequiredArrayIfChanged(role, "requiredCapabilities", edit.RequiredCapabilities);
		PatchFlagBlock(role, "spawn", "allowed", "allowedRoles", edit.SpawnAllowed, edit.SpawnAllowedRoles);
		PatchFlagBlock(role, "escalation", "available", "targets", edit.EscalationAvailable, edit.EscalationTargets);
		SetOptionalStringIfChanged(role, "notes", edit.Notes);

		return root.ToJsonString(Options);
	}

	// Append a new minimal role (the same shape ProjectAgentDefsModel.StarterJson uses for a
	// fresh definition) — only ever ADDS to the roles array, never touches an existing role or
	// any property outside the schema.
	public static string AddRole(string rawJson, string slug)
	{
		var root = ParseRoot(rawJson);
		var roles = RolesArray(root);
		roles.Add(new JsonObject
		{
			["slug"] = slug,
			["tier"] = "worker",
			["requiredCapabilities"] = new JsonArray(),
			["spawn"] = new JsonObject { ["allowed"] = false },
			["escalation"] = new JsonObject { ["available"] = false },
		});
		return root.ToJsonString(Options);
	}

	// Remove the role at roleIndex. Refuses to drop the last role — AgentDefinitionJson.Validate
	// requires at least one, and a raw write that failed that check would just bounce back with
	// the definition already half-edited in the browser.
	public static string RemoveRole(string rawJson, int roleIndex)
	{
		var root = ParseRoot(rawJson);
		var roles = RolesArray(root);
		if (roleIndex < 0 || roleIndex >= roles.Count)
			throw new ArgumentException($"role index {roleIndex} no longer exists — reload and try again");
		if (roles.Count <= 1)
			throw new ArgumentException("a definition must keep at least one role — add a replacement before deleting the last one");
		roles.RemoveAt(roleIndex);
		return root.ToJsonString(Options);
	}

	static JsonObject ParseRoot(string rawJson)
	{
		var node = JsonNode.Parse(rawJson);
		if (node is not JsonObject obj)
			throw new ArgumentException("agent definition body must be a JSON object");
		return obj;
	}

	static JsonArray RolesArray(JsonObject root) =>
		root["roles"] as JsonArray
			?? throw new ArgumentException("agent definition body must carry a 'roles' array");

	static JsonObject RoleAt(JsonObject root, int roleIndex)
	{
		var roles = RolesArray(root);
		if (roleIndex < 0 || roleIndex >= roles.Count || roles[roleIndex] is not JsonObject role)
			throw new ArgumentException($"role index {roleIndex} no longer exists — reload and try again");
		return role;
	}

	static void SetStringIfChanged(JsonObject obj, string prop, string value)
	{
		if (obj[prop] is JsonValue v && v.TryGetValue<string>(out var s) && s == value) return;
		obj[prop] = value;
	}

	// notes: absent key == null value == empty string, all mean "no notes" — never write an
	// empty string, mirroring the typed path (a role without notes never serializes the key).
	static void SetOptionalStringIfChanged(JsonObject obj, string prop, string? value)
	{
		var normalized = string.IsNullOrEmpty(value) ? null : value;
		var current = obj[prop];
		if (normalized is null)
		{
			if (current is null) return;
			obj.Remove(prop);
			return;
		}
		if (current is JsonValue v && v.TryGetValue<string>(out var s) && s == normalized) return;
		obj[prop] = normalized;
	}

	// requiredCapabilities is REQUIRED (Validate throws on a null list) — an empty selection
	// still writes `[]`, it never removes the key.
	static void SetRequiredArrayIfChanged(JsonObject obj, string prop, IReadOnlyList<string> value)
	{
		if (StringArrayEquals(obj[prop] as JsonArray, value)) return;
		obj[prop] = ToJsonArray(value);
	}

	// spawn / escalation: `{ <flag>: bool, <list>?: string[] }`. Absent block == flag false +
	// empty list. Only materializes (or keeps) the block when the computed state actually
	// differs from what is already there — a no-op save on an absent block stays absent, and a
	// no-op save on a present block leaves it (and any property on it this code doesn't know
	// about) untouched.
	static void PatchFlagBlock(
		JsonObject role, string blockName, string flagName, string listName,
		bool flagValue, IReadOnlyList<string> listValue)
	{
		var block = role[blockName] as JsonObject;
		var flagCurrent = block?[flagName] is JsonValue fv && fv.TryGetValue<bool>(out var b) && b;
		var listCurrent = block?[listName] as JsonArray;

		var sameFlag = flagCurrent == flagValue;
		var sameList = StringArrayEquals(listCurrent, listValue);
		if (sameFlag && sameList) return;

		// The new state IS the absent-block default (flag false, list empty): drop the whole
		// block rather than leave a `{ "allowed": false }` husk behind — that keeps "uncheck
		// everything" produce the same shape a role that never had the block does, instead of
		// depending on which one the document happened to start from.
		if (!flagValue && listValue.Count == 0)
		{
			if (block is not null) role.Remove(blockName);
			return;
		}

		block ??= (JsonObject)(role[blockName] = new JsonObject());
		if (!sameFlag) block[flagName] = flagValue;
		if (!sameList)
		{
			if (listValue.Count == 0) block.Remove(listName);
			else block[listName] = ToJsonArray(listValue);
		}
	}

	static JsonArray ToJsonArray(IReadOnlyList<string> value) =>
		new([.. value.Select(s => (JsonNode)JsonValue.Create(s))]);

	static bool StringArrayEquals(JsonArray? arr, IReadOnlyList<string> value)
	{
		if (arr is null) return value.Count == 0;
		if (arr.Count != value.Count) return false;
		for (var i = 0; i < arr.Count; i++)
		{
			if (arr[i] is not JsonValue v || !v.TryGetValue<string>(out var s) || s != value[i]) return false;
		}
		return true;
	}

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
