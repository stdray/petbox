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

	// A registered tool's OutputSchema is generated by the SAME options as InputSchema (mcpJson,
	// UnmappedMemberHandling.Disallow), so it inherits `additionalProperties:false` on every closed
	// object node as a SIDE EFFECT — never a decision about the wire contract. Input needs the
	// closure (a strict client should catch an unknown ARGUMENT locally, pinned by
	// InputSchema_ClosesTheObjectNodes_AndLeavesTheOpenDictionaryOpen); OUTPUT must NOT have it: an
	// MCP client caches a tool's output-schema validator across `listTools()` calls and only
	// refreshes it on the next explicit re-list, so a deploy that ADDS a field to some tool's
	// response makes every session that already cached the OLD (closed) schema reject the new
	// (correct) response with "data must NOT have additional properties" — even though the write
	// it responded to went through. Hit twice in prod: 2026-08-30 (slug/url in comments_*) and
	// 2026-09-08 (similar/similarityUnavailable in memory_*).
	//
	// Card mcp-output-schema-drop-additional-properties-false; full mechanics, why the shared
	// TransformSchemaNode (used for date-time-format stripping) is NOT the right seam here, and the
	// rejected alternatives (schema/tool-name versioning, listen/list_changed) are in
	// observations/mcp-response-field-addition-breaks-live-sessions comment
	// f51bdd6b7c3c42b69c91b27cd184291e.
	//
	// Deliberately does NOT touch InputSchema, and does NOT run inside NullableAware's
	// TransformSchemaNode (which is shared by both schemas) — either would reopen input arguments
	// too and break the input-closure test. Strictness itself is not lost: the CI conformance test
	// (McpOutputSchemaConformanceTests) still validates each tool's structuredContent against a
	// LOCALLY re-closed copy of this now-open schema, so a divergence between the declared
	// OutputSchemaType and what the tool actually returns still fails CI — only the wire-visible
	// copy stays open.
	static McpServerTool WithOpenOutputSchema(McpServerTool tool)
	{
		if (tool.ProtocolTool.OutputSchema is not { } outputSchema) return tool;
		if (JsonNode.Parse(outputSchema.GetRawText()) is not JsonObject schema) return tool;
		OpenClosedObjectNodes(schema);
		tool.ProtocolTool.OutputSchema = JsonSerializer.SerializeToElement(schema);
		return tool;
	}

	// Recursively drop an `additionalProperties:false` member wherever it appears in the tree — a
	// nested object (e.g. an array item inside `data`/`updated`) carries its OWN copy, not just the
	// schema root. Everything else (types, `required`, nullability, an open dictionary's
	// `additionalProperties: <subschema>`) is left untouched.
	static void OpenClosedObjectNodes(JsonNode? node)
	{
		switch (node)
		{
			case JsonObject obj:
				if (obj["additionalProperties"] is JsonValue v && v.TryGetValue<bool>(out var isFalse) && !isFalse)
					obj.Remove("additionalProperties");
				foreach (var property in obj.ToList())
					OpenClosedObjectNodes(property.Value);
				break;
			case JsonArray arr:
				foreach (var item in arr)
					OpenClosedObjectNodes(item);
				break;
		}
	}

	// ── no tool ships with an EMPTY `properties` (work qwen-subagent-spawn-json-parse-error-go-gateway) ──
	//
	// LIVE INCIDENT 09.09.2026, twice in one afternoon: a qwen-code subagent on the opencode-go arm
	// died mid-run with
	//
	//     400 [json_parse_error] Invalid JSON data: Failed to deserialize the JSON body into the
	//     target type: tools[74].function: missing field `parameters`
	//
	// A DESERIALIZATION failure, not a syntax one — the gateway parsed the whole body and refused one
	// tool object for a missing REQUIRED field. Measured, not inferred: a 180-byte request reproduces
	// it, so size and truncation are not involved, and an A/B over 106 turns put it at 2/56 refusals
	// without `parameters` against 0/50 with it. Intermittent (~4% per turn — the gateway fans out to
	// a pool of upstreams and only some deserialize strictly), which is exactly why the same run
	// "sometimes works": a 20-turn session survives about half the time.
	//
	// THE MECHANISM IS ON THE CLIENT SIDE, and our schema is valid on its own: qwen-code converts an
	// MCP tool to its wire form with `...parameters !== void 0 ? { parameters } : {}`, and it derives
	// `undefined` from a schema whose `properties` is EMPTY. A no-argument tool therefore leaves this
	// server with a perfectly legal `{"type":"object","properties":{}}` and arrives at the gateway
	// with no `parameters` member at all. We cannot patch qwen-code or the gateway; we CAN make sure
	// the shape that triggers it never leaves here.
	//
	// WHY A REGISTRATION-STAGE GUARD AND NOT A HAND-ADDED PARAMETER PER TOOL. Both were on the table.
	// A real optional C# parameter on `whoami` and `deploy_node_list` fixes exactly today's two tools
	// and re-arms the mine the next time someone writes a no-argument verb — and the parameter would
	// be a lie in the signature, since no tool body wants it. This is a WIRE-COMPATIBILITY workaround,
	// the same species as the three transforms above it (strict-client `required` pruning, the
	// date-time `format` strip, the open output schema), and it belongs where they are: applied to
	// every registered tool, at creation, with no per-tool discipline to forget.
	//
	// The injected property is OPTIONAL (absent from `required`) and IGNORED: the SDK binder pulls
	// each C# parameter out of the arguments dict by name and never reads a key that matches none, so
	// a caller that omits it — every existing caller — is byte-for-byte unaffected, and a caller that
	// sends it gets the same answer. It is declared in `properties` precisely so
	// McpUnknownParameterFilter (which refuses top-level keys absent from the schema) accepts it
	// rather than rejecting a client that echoes the schema back.
	//
	// `type` is a plain "string", not a `["string","null"]` union: a union is one more shape for a
	// strict or lossy client converter to mishandle, and optionality is already carried by the
	// absence from `required`. Pinned end-to-end by McpEmptyInputSchemaTests over a real tools/list.
	internal const string EmptyPropertiesPlaceholder = "_unused";

	internal const string EmptyPropertiesPlaceholderDescription =
		"Ignored — this tool takes no arguments. The property exists only because some MCP clients "
		+ "(qwen-code) drop the `parameters` field entirely when a tool's `properties` is empty, and "
		+ "some upstream gateways then reject the whole request with "
		+ "\"missing field `parameters`\". Pass nothing; a value is accepted and discarded.";

	// Give an EMPTY (or absent) `properties` object exactly one optional, ignored member. A schema
	// that already declares any property is returned untouched — this only ever fires on a
	// no-argument tool. Returns true when it changed the node, so a caller can skip reserializing.
	public static bool EnsureNonEmptyProperties(JsonObject schema)
	{
		if (schema["properties"] is JsonObject { Count: > 0 }) return false;
		schema["properties"] = new JsonObject
		{
			[EmptyPropertiesPlaceholder] = new JsonObject
			{
				["type"] = "string",
				["description"] = EmptyPropertiesPlaceholderDescription,
			},
		};
		return true;
	}

	static McpServerTool WithNonEmptyInputProperties(McpServerTool tool)
	{
		if (JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) is not JsonObject schema) return tool;
		if (!EnsureNonEmptyProperties(schema)) return tool;
		tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
		return tool;
	}

	// EVERY post-generation shaping a registered tool gets, in one place and in ORDER: the two
	// attribute-driven input rewrites first (they read the schema the generator produced), then the
	// output schema is opened, then the empty-`properties` guard has the last word on the input
	// schema — it must see the FINAL shape, or a transform above it could still hand the wire an
	// empty object. Applied identically to static and instance tools; before this the whole chain was
	// spelled out twice, nested four deep, which is how a transform gets added to one arm only.
	static McpServerTool Shape(McpServerTool tool, MethodInfo method) =>
		WithNonEmptyInputProperties(WithOpenOutputSchema(WithRequiredMembers(WithDeclaredShapes(tool, method), method)));

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
					? services => Shape(McpServerTool.Create(mi, options: new()
					{
						Services = services,
						SerializerOptions = serializerOptions,
						SchemaCreateOptions = schemaOptions,
					}), mi)
					: services => Shape(McpServerTool.Create(mi, r => r.Services is { } sp
						? ActivatorUtilities.CreateInstance(sp, tt)
						: Activator.CreateInstance(tt)!, new()
						{
							Services = services,
							SerializerOptions = serializerOptions,
							SchemaCreateOptions = schemaOptions,
						}), mi)));
			}
		}

		return builder;
	}
}
