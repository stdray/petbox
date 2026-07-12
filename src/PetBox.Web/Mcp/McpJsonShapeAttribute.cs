namespace PetBox.Web.Mcp;

// Declares the JSON type of a raw-payload tool parameter (`JsonElement` / `JsonElement?`),
// which the schema exporter would otherwise export as the boolean schema `true` — a property
// with a description and NO `type`. Consumed by McpOutputSchema.NullableAware's
// TransformSchemaNode, which stamps `type` onto the generated node.
//
//   [McpJsonShape("object")]          -> "type": "object"
//   [McpJsonShape("array", "null")]   -> "type": ["array", "null"]   (an optional payload)
//
// Types are JSON Schema type names ("object", "array", …) — not CLR names. Declare the shape
// the tool body actually parses; a lie here is worse than no type at all.
[AttributeUsage(AttributeTargets.Parameter)]
sealed class McpJsonShapeAttribute(params string[] types) : Attribute
{
	public IReadOnlyList<string> Types { get; } = types.Length > 0
		? types
		: throw new ArgumentException("at least one JSON type is required", nameof(types));
}
