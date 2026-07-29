using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
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
		TransformSchemaNode = static (_, node) =>
		{
			if (node is JsonObject obj)
			{
				PruneNullableRequired(obj);
				StripDateTimeFormat(obj);
			}
			return node;
		},
	};

	// A `JsonElement` / `JsonElement?` tool parameter (a raw JSON payload — an agent-definition
	// document, an LLM registry, a message array) has NO CLR shape, so the generated input schema
	// declares it with a `description` and NO `type` (STJ exports the boolean schema `true`, i.e.
	// "anything goes"). A strict client then has nothing to bind to and falls back to sending the
	// payload double-encoded as a JSON *string* — which is how agent_def_upsert became uncallable
	// over MCP (intake mcp-agent-def-upsert-definition-param-untyped: `JsonException: The JSON
	// value could not be converted to AgentDefinitionDoc. Path: $`).
	//
	// [McpJsonShape] declares the JSON type such a parameter really accepts, and this stamps it
	// into the generated schema at tool-CREATION time (not per-request, not in the description).
	// It is done here rather than in TransformSchemaNode because the schema-create context does not
	// carry the ParameterInfo for a function parameter (MEAI builds parameter schemas from the type
	// + description only), so the attribute is invisible from inside the transform — the MethodInfo
	// we already hold is the only place that sees it.
	static McpServerTool WithDeclaredShapes(McpServerTool tool, MethodInfo method)
	{
		var shaped = method.GetParameters()
			.Select(p => (p.Name, Shape: p.GetCustomAttribute<McpJsonShapeAttribute>()))
			.Where(p => p.Shape is not null)
			.ToList();
		if (shaped.Count == 0) return tool;

		if (JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) is not JsonObject schema
			|| schema["properties"] is not JsonObject properties)
			return tool;

		foreach (var (name, shape) in shaped)
		{
			if (properties[name!] is not JsonObject prop)
				throw new InvalidOperationException(
					$"[McpJsonShape] on {method.Name}({name}): no such property in the generated input schema");
			prop["type"] = shape!.Types.Count == 1
				? JsonValue.Create(shape.Types[0])
				: new JsonArray(shape.Types.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
		}

		tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
		return tool;
	}

	// Stamp [McpRequiredMember] onto the generated input schema: the member joins its object's
	// `required` array and loses the "null" arm of its type union, so a strict client validates the
	// same contract the tool body enforces. See McpRequiredMemberAttribute for why this is post-hoc
	// and why the CLR property stays nullable.
	//
	// Runs AFTER generation (so NullableAware's PruneNullableRequired cannot strip it straight back
	// out) and looks only where a batch verb actually puts its items: a parameter whose type is
	// T[]/IEnumerable<T>, descending one level through the array schema's `items`. A parameter of a
	// plain object type is handled too (no `items` hop).
	//
	// A miss THROWS at tool-creation (= startup), never silently no-ops: a required marker that
	// quietly failed to land would be exactly the schema-lies-about-the-contract bug this exists to
	// end.
	static McpServerTool WithRequiredMembers(McpServerTool tool, MethodInfo method)
	{
		var targets = method.GetParameters()
			.Select(p => (p.Name, Members: RequiredMembersOf(ElementTypeOf(p.ParameterType))))
			.Where(p => p.Members.Count > 0)
			.ToList();
		if (targets.Count == 0) return tool;

		if (JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) is not JsonObject schema
			|| schema["properties"] is not JsonObject properties)
			throw new InvalidOperationException(
				$"[McpRequiredMember] on {method.Name}: the generated input schema has no `properties`");

		foreach (var (param, members) in targets)
		{
			if (properties[param!] is not JsonObject paramSchema)
				throw new InvalidOperationException(
					$"[McpRequiredMember] on {method.Name}({param}): no such property in the generated input schema");
			// Array parameter → the item object carries the members; object parameter → itself.
			var owner = paramSchema["items"] as JsonObject ?? paramSchema;
			if (owner["properties"] is not JsonObject ownerProps)
				throw new InvalidOperationException(
					$"[McpRequiredMember] on {method.Name}({param}): the item schema has no `properties` " +
					"(a $ref/$defs indirection is not supported here)");

			var required = owner["required"] as JsonArray;
			if (required is null) owner["required"] = required = [];

			foreach (var member in members)
			{
				if (ownerProps[member] is not JsonObject memberSchema)
					throw new InvalidOperationException(
						$"[McpRequiredMember] on {method.Name}({param}): the item schema has no `{member}` property");
				// Drop the "null" arm: the schema must not offer a value the tool body rejects.
				if (memberSchema["type"] is JsonArray types)
				{
					var concrete = types.Select(t => t?.GetValue<string>())
						.Where(t => t is not null and not "null").ToList();
					if (concrete.Count == 1) memberSchema["type"] = JsonValue.Create(concrete[0]);
					else if (concrete.Count > 1)
						memberSchema["type"] = new JsonArray(concrete.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
				}
				if (!required.Any(r => r?.GetValue<string>() == member))
					required.Add(JsonValue.Create(member));
			}
		}

		tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
		return tool;
	}

	// The wire (camelCase) names of a type's [McpRequiredMember] properties.
	static List<string> RequiredMembersOf(Type? type) =>
		type is null || type.IsPrimitive || type == typeof(string)
			? []
			: type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Where(p => p.GetCustomAttribute<McpRequiredMemberAttribute>() is not null)
				.Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
				.ToList();

	// T[] / IEnumerable<T> → T; anything else → itself.
	static Type? ElementTypeOf(Type type)
	{
		if (type.IsArray) return type.GetElementType();
		if (type.IsGenericType && type.GetGenericArguments() is [var arg]
			&& typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
			return arg;
		return type;
	}

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
					? services => WithRequiredMembers(WithDeclaredShapes(McpServerTool.Create(mi, options: new()
					{
						Services = services,
						SerializerOptions = serializerOptions,
						SchemaCreateOptions = schemaOptions,
					}), mi), mi)
					: services => WithRequiredMembers(WithDeclaredShapes(McpServerTool.Create(mi, r => r.Services is { } sp
						? ActivatorUtilities.CreateInstance(sp, tt)
						: Activator.CreateInstance(tt)!, new()
						{
							Services = services,
							SerializerOptions = serializerOptions,
							SchemaCreateOptions = schemaOptions,
						}), mi), mi)));
			}
		}

		return builder;
	}
}
