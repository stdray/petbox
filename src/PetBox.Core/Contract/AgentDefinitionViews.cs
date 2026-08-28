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

// One role's PARTIAL edit, as submitted to the MERGE path (MCP agent_def_upsert). `Slug` is the
// IDENTITY — which role this edit addresses, matched against the stored document's roles by slug.
// Every other field is NULLABLE and means "leave whatever the stored role already has": the same
// omit=keep convention TaskNodeInput/MemoryEntryInputDto carry, and the reason the whole document
// no longer has to travel to change one field.
//
// This is an INTERNAL normalized form, NOT a wire shape. The MCP parameter (AgentDefDocumentInput /
// AgentDefRoleInput in PetBox.Web.Mcp.Contract) is a single typed NESTED document that mirrors what
// agent_def_get emits — `{ name, roles:[{ slug, …, spawn:{allowed, allowedRoles},
// escalation:{available, targets}, notes }] }` — so a read can be pasted back into a write without
// reshaping. The tool flattens the two blocks into this record on the way in, and MergeRoles
// restores the nesting on the way out, keeping the flag/list pair adjacent for MergeFlagBlock, which
// has to consider both halves together.
//
// It is flat here for ONE reason only: MergeFlagBlock takes the pair. It is NOT flat for schema
// reach — an earlier draft of this card flattened the WIRE shape because McpUnknownParameterFilter
// walked exactly one hop and could not police a nested object. That constraint is gone
// (work/mcp-unmapped-member-disallow): the MCP serializer options carry
// UnmappedMemberHandling.Disallow, so a typo inside `spawn` is refused BY THE TYPE, at any depth.
// Do not resurrect a flat wire shape citing the filter — it no longer owns that guarantee.
public sealed record RoleMergeEdit(
	string Slug,
	string? Tier = null,
	IReadOnlyList<string>? RequiredCapabilities = null,
	bool? SpawnAllowed = null,
	IReadOnlyList<string>? SpawnAllowedRoles = null,
	bool? EscalationAvailable = null,
	IReadOnlyList<string>? EscalationTargets = null,
	string? Notes = null,
	bool Deleted = false);

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

	// MERGE-BY-ROLE (work/agent-def-upsert-typed-and-merge-by-role). Apply a set of PARTIAL role
	// edits to the document AS STORED and return the new raw JSON.
	//
	// The contract, in one line: a role you do not send is LEFT ALONE, and a field you do not set on
	// a role you DO send is left alone too. Absence stopped meaning "delete" — deletion is now the
	// explicit `deleted:true` marker, mirroring tasks_upsert/memory_upsert, so a payload that lost a
	// role in transit (or a caller that only knew about three of six roles) can no longer erase the
	// rest. Under the old full-replace that same call silently dropped every role it omitted.
	//
	// Operates on the JsonNode tree of the STORED document rather than on the typed record, for the
	// same reason UpsertJsonAsync does: properties outside the typed schema — on the root, on ANOTHER
	// role, or on the edited role itself — survive a merge instead of being erased by a round trip
	// through AgentDefinitionDoc. A caller editing `worker` cannot damage `orchestrator` at all.
	//
	// An unknown slug APPENDS a new role. It is not an error: create-a-role and edit-a-role are the
	// same call, exactly as tasks_upsert makes create and patch one verb, and the required-field
	// check (Validate, at the service) is what catches a new role that arrives half-specified.
	public static string MergeRoles(string rawJson, string? name, IReadOnlyList<RoleMergeEdit> edits)
	{
		if (edits is null || edits.Count == 0)
			throw new ArgumentException("'roles': empty batch — nothing to write");

		var root = ParseRoot(rawJson);
		// A null/blank name means "keep the stored one" — the same omit=keep rule the role fields
		// follow. CanonicalizeRaw fills the key slug in when the document has no name at all.
		if (!string.IsNullOrWhiteSpace(name)) SetStringIfChanged(root, "name", name);

		var roles = root["roles"] as JsonArray;
		if (roles is null) root["roles"] = roles = [];

		// Two edits for one slug in ONE call would apply in array order and leave the caller unable to
		// tell which one won — refuse instead of silently picking the last, the same stance
		// tasks_upsert takes on a duplicated key.
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var edit in edits)
		{
			var slug = edit.Slug?.Trim() ?? "";
			if (slug.Length == 0)
				throw new ArgumentException("each role needs a 'slug' (it identifies which role this edit applies to)");
			if (!seen.Add(slug))
				throw new ArgumentException($"role '{slug}' appears twice in one call — send one edit per role");

			var index = IndexOfRole(roles, slug);

			if (edit.Deleted)
			{
				// Deleting a role that is not there is a NO-OP, not an error: the same idempotent
				// stance agent_def_delete takes on a missing key.
				if (index >= 0) roles.RemoveAt(index);
				continue;
			}

			JsonObject role;
			if (index >= 0)
			{
				role = (JsonObject)roles[index]!;
			}
			else
			{
				role = new JsonObject { ["slug"] = slug };
				roles.Add(role);
			}

			if (edit.Tier is not null) SetStringIfChanged(role, "tier", edit.Tier);
			if (edit.RequiredCapabilities is not null)
				SetCapabilities(role, edit.RequiredCapabilities);
			MergeFlagBlock(role, "spawn", "allowed", "allowedRoles", edit.SpawnAllowed, edit.SpawnAllowedRoles);
			MergeFlagBlock(role, "escalation", "available", "targets", edit.EscalationAvailable, edit.EscalationTargets);
			if (edit.Notes is not null) SetOptionalStringIfChanged(role, "notes", edit.Notes);
		}

		return root.ToJsonString(Options);
	}

	// The stored role carrying this slug, or -1. Slug comparison is ORDINAL — the document's own
	// slugs are already lowercase by convention and a case-folding match here would let `Worker` and
	// `worker` address the same role in one call while Validate still sees two.
	static int IndexOfRole(JsonArray roles, string slug)
	{
		for (var i = 0; i < roles.Count; i++)
		{
			if (roles[i] is JsonObject o
				&& o["slug"] is JsonValue v
				&& v.TryGetValue<string>(out var s)
				&& s == slug)
				return i;
		}
		return -1;
	}

	// PARTIAL form of PatchFlagBlock: either half of a spawn/escalation block may be null = keep. The
	// half that is not sent is read back off the STORED block, so PatchFlagBlock still sees a
	// complete state and its absent-block/husk rules stay in ONE place.
	static void MergeFlagBlock(
		JsonObject role, string blockName, string flagName, string listName,
		bool? flag, IReadOnlyList<string>? list)
	{
		if (flag is null && list is null) return; // neither half sent — the block is not this call's business
		var block = role[blockName] as JsonObject;
		var flagCurrent = block?[flagName] is JsonValue fv && fv.TryGetValue<bool>(out var b) && b;
		var listCurrent = ReadStringArray(block?[listName] as JsonArray);
		PatchFlagBlock(role, blockName, flagName, listName, flag ?? flagCurrent, list ?? listCurrent);
	}

	// requiredCapabilities on the MERGE path. Not SetRequiredArrayIfChanged: that one treats an
	// ABSENT key and an empty list as equal, which is right for the form (the key is always there —
	// Validate requires it) and wrong here, where a brand-new role starts as `{ "slug": … }` alone.
	// An explicit `requiredCapabilities: []` on a new role must WRITE `[]`, otherwise the role is
	// stored without the field and Validate rejects the caller's own well-formed payload.
	static void SetCapabilities(JsonObject role, IReadOnlyList<string> value)
	{
		if (role["requiredCapabilities"] is JsonArray existing && StringArrayEquals(existing, value)) return;
		role["requiredCapabilities"] = ToJsonArray(value);
	}

	static List<string> ReadStringArray(JsonArray? arr)
	{
		if (arr is null) return [];
		var list = new List<string>(arr.Count);
		foreach (var item in arr)
			if (item is JsonValue v && v.TryGetValue<string>(out var s))
				list.Add(s);
		return list;
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
