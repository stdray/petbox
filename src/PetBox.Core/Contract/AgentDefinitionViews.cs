using System.Text.Json;
using System.Text.Json.Serialization;

namespace PetBox.Core.Contract;

// Portable agent-definition document (agent-definition-as-data). Server stores the
// roster only — role → model binding and the owner ($HOME) axis are local, not columns.
// JSON wire: camelCase. Unknown properties are ignored for forward-compat EXCEPT
// role.model, which is rejected (portable definitions must not carry models).

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

	// Parse a definition document from JSON. Rejects role.model (portable roster only).
	// Other unknown properties are ignored (forward-compat).
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
	static void RejectModelField(JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object) return;
		if (root.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array)
		{
			foreach (var role in roles.EnumerateArray())
			{
				if (role.ValueKind == JsonValueKind.Object && role.TryGetProperty("model", out _))
					throw new ArgumentException(
						"role.model is not allowed on portable agent definitions — model binding is local, not part of the definition document");
			}
		}
	}
}
