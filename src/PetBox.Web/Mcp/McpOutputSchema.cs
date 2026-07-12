using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace PetBox.Web.Mcp;

// Schema-honest tool registration.
//
// The MCP tool serializer omits null-valued keys (DefaultIgnoreCondition =
// WhenWritingNull — a deliberate token economy, incl. the bodyLen contract that
// drops Body when null). But the SDK's SDK-generated outputSchema marks EVERY
// record property `required` (a positional record's ctor params are all
// non-optional, so STJ requires them all — nullability is orthogonal). A strict
// client (opencode/DeepSeek) then rejects our structuredContent with
// "must have required property 'body'" because the omitted null key is
// absent yet declared required.
//
// Fix: generate an HONEST schema — nullable properties are NOT required (they
// carry a `["T","null"]` type union in the generated schema, so null-omission is
// legal). We keep null-omission untouched. This is done via an
// AIJsonSchemaCreateOptions.TransformSchemaNode that, for every object node,
// prunes from its `required` array any property whose own schema admits null.
//
// The SDK's WithToolsFromAssembly overload only accepts a JsonSerializerOptions,
// with no seam for AIJsonSchemaCreateOptions, so we replicate its (small)
// assembly scan here and thread the schema options through McpServerTool.Create.
// Behaviour is otherwise identical to the SDK: static tool methods bind directly;
// instance methods get a per-call target built from the request service provider.
static class McpOutputSchema
{
	// Shared schema-generation options: honest `required` for nullable properties.
	// Applies to both the input schema (nullable params become optional — more
	// lenient, and correct) and the output schema (the actual fix).
	public static readonly AIJsonSchemaCreateOptions NullableAware = new()
	{
		TransformSchemaNode = static (ctx, node) =>
		{
			if (node is JsonObject obj)
			{
				PruneNullableRequired(obj);
				StripDateTimeFormat(obj);
			}
			return node;
		},
	};

	// Prune from an object node's `required` array any property whose own schema admits null,
	// so our WhenWritingNull omission of a null value stays schema-conformant.
	static void PruneNullableRequired(JsonObject obj)
	{
		if (obj["required"] is not JsonArray required || obj["properties"] is not JsonObject properties)
			return;
		for (var i = required.Count - 1; i >= 0; i--)
		{
			if (required[i]?.GetValue<string>() is { } name
				&& properties[name] is JsonObject propSchema
				&& AllowsNull(propSchema))
			{
				required.RemoveAt(i);
			}
		}
		if (required.Count == 0) obj.Remove("required");
	}

	// Drop a `format:"date-time"` annotation from a property schema. Timestamps come from SQLite as
	// DateTimeKind.Unspecified and serialize zone-less ("2026-07-04T12:58:10.238") — NOT a valid
	// RFC 3339 date-time — so strict clients (Factory Droid / opencode, ajv, which ENFORCE format)
	// reject every tool that returns a timestamp with -32602 "must match format date-time". In draft
	// 2020-12 `format` is an annotation, not an assertion (Claude Code's Zod already ignores it), so
	// dropping it keeps the field validated as a plain string and unbreaks the strict clients without
	// touching values or nullability. Applied per-node on the same schema walk.
	public static void StripDateTimeFormat(JsonObject obj)
	{
		if (obj["format"]?.GetValue<string>() == "date-time")
			obj.Remove("format");
	}

	// A property schema admits null iff its `type` is an array containing "null"
	// (how STJ/MEAI renders a nullable T — scalar, array, or complex object all
	// become `["<t>","null"]`). Non-array `type` (a plain non-null type) or a bare
	// $ref (no local type) is treated as required — safe default: never drop a key
	// we are unsure about.
	static bool AllowsNull(JsonObject schema) =>
		schema["type"] is JsonArray types && types.Any(t => t?.GetValue<string>() == "null");

	// Mirror of ModelContextProtocol's WithToolsFromAssembly, plus SchemaCreateOptions.
	public static IMcpServerBuilder WithSchemaHonestToolsFromAssembly(
		this IMcpServerBuilder builder,
		Assembly assembly,
		JsonSerializerOptions serializerOptions,
		AIJsonSchemaCreateOptions schemaOptions)
	{
		foreach (var toolType in assembly.GetTypes())
		{
			if (toolType.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

			foreach (var method in toolType.GetMethods(
				BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
			{
				if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;

				var mi = method;
				var tt = toolType;
				builder.Services.AddSingleton((Func<IServiceProvider, McpServerTool>)(mi.IsStatic
					? services => McpServerTool.Create(mi, options: new()
					{
						Services = services,
						SerializerOptions = serializerOptions,
						SchemaCreateOptions = schemaOptions,
					})
					: services => McpServerTool.Create(mi, r => r.Services is { } sp
						? ActivatorUtilities.CreateInstance(sp, tt)
						: Activator.CreateInstance(tt)!, new()
						{
							Services = services,
							SerializerOptions = serializerOptions,
							SchemaCreateOptions = schemaOptions,
						})));
			}
		}

		return builder;
	}
}
