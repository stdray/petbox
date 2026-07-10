using System.Text.Json;
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

public sealed record AgentDefinitionRole(
	string Slug,
	string Tier,
	IReadOnlyList<string> RequiredCapabilities,
	AgentDefinitionSpawn? Spawn = null,
	AgentDefinitionEscalation? Escalation = null);

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
