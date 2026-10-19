using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PetBox.Web.Mcp;

// The committed, machine-readable description of the MCP surface — the MCP half of spec
// `typed-surface` / `contract-doc-in-git`. Until this existed only the REST half was visible in
// `doc/api/`: adding, renaming or re-typing any of the ~99 tools changed NOTHING in a diff, because
// MCP publishes its schemas only at runtime (`tools/list`, per-key filtered) and never as a document.
//
// HONESTY ABOUT THE SHAPE — read this before generating a client from it. The paths below are NOT
// HTTP routes. MCP has exactly ONE endpoint (`POST /mcp`, JSON-RPC `tools/call`); one logical path per
// tool exists so that ordinary OpenAPI tooling (client generators, Swagger UI, contract diffs) has
// something to consume, and each is the same single POST with a different `params.name`. The
// description says this in words too, because a document that quietly implies 99 working routes
// would be the kind of lie this repo keeps filing intake cards about.
//
// DETERMINISM is a requirement, not a nicety: identical surface ⇒ byte-identical file, so the diff
// shows drift and only drift. Everything is emitted in ordinal order (paths, and the scope list),
// and JsonObject preserves insertion order, so no sort of the serialized output is ever needed.
static class McpOpenApiDocument
{
	const string Title = "PetBox MCP surface";
	const string Version = "1.0.0";

	const string Description =
		"Logical description of the PetBox MCP tool surface. These paths are NOT HTTP routes: MCP is "
		+ "served by the single stateless endpoint POST /mcp as JSON-RPC 2.0, and each path here is the "
		+ "same call with a different params.name ({\"method\":\"tools/call\",\"params\":{\"name\":\"<tool>\","
		+ "\"arguments\":{...}}}). They are modelled as paths so that standard OpenAPI tooling — client "
		+ "generators, Swagger UI, contract diffs — can consume the surface. Authentication is an API key "
		+ "in the X-Api-Key header; which key may call a tool is declared per operation in "
		+ "x-required-scopes / x-scope-kind. Bodies follow the same null-omission and bodyLen contracts "
		+ "the live server applies. "
		+ "KEY-INDEPENDENT, AND WHY projectKey LOOKS REQUIRED: this document describes the MAXIMAL schema, "
		+ "so every parameter of the tool is listed and `projectKey` is required. A key scoped to exactly "
		+ "one project may omit it — the server substitutes the key's own project — and the live per-key "
		+ "schema (tools/list) says so in that parameter's description and drops it from `required`. "
		+ "Passing it explicitly is always accepted, so a client generated from this document works with "
		+ "any key. Every other parameter and every output schema is identical to what the server "
		+ "publishes for any key.";

	public static JsonObject Build(Assembly assembly, JsonSerializerOptions serializerOptions, IServiceProvider services)
	{
		var paths = new JsonObject();
		var tools = McpOutputSchema.ToolMethods(assembly)
			.Select(pair => McpOutputSchema.CreateForDocumentation(pair.Method, serializerOptions, services))
			.Select(t => t.ProtocolTool)
			.OrderBy(t => t.Name, StringComparer.Ordinal);

		// A path item is `{ post: <operation> }` — the OPERATION is nested, not the item itself. Getting
		// this wrong produces a document that parses as JSON and is not OpenAPI at all (the first draft
		// did exactly that).
		foreach (var tool in tools) paths[$"/tools/{tool.Name}"] = new JsonObject { ["post"] = Operation(tool) };

		return new JsonObject
		{
			["openapi"] = "3.1.0",
			["info"] = new JsonObject
			{
				["title"] = Title,
				["version"] = Version,
				["description"] = Description,
			},
			["security"] = new JsonArray { new JsonObject { ["apiKey"] = new JsonArray() } },
			["paths"] = paths,
			["components"] = new JsonObject
			{
				["securitySchemes"] = new JsonObject
				{
					["apiKey"] = new JsonObject
					{
						["type"] = "apiKey",
						["in"] = "header",
						["name"] = "X-Api-Key",
						["description"] =
							"An ApiKey minted for the target project (or a cross-project key with a default "
							+ "project). A project-scoped key supplies the project implicitly; a tool's "
							+ "`projectKey` is omitted then and may be omitted in these bodies too.",
					},
				},
				["schemas"] = new JsonObject
				{
					// The one error shape every tool can return (Mcp/McpErrorEnvelopeFilter.cs). Kept in
					// `components` rather than repeated 99 times: the envelope is the server's, not the tool's.
					["McpError"] = new JsonObject
					{
						["type"] = "object",
						["description"] = "Structured MCP error envelope (never a raw exception or a stack trace).",
						["properties"] = new JsonObject
						{
							["error"] = new JsonObject
							{
								["type"] = "object",
								["properties"] = new JsonObject
								{
									["type"] = new JsonObject { ["type"] = "string" },
									["message"] = new JsonObject { ["type"] = "string" },
									["detail"] = new JsonObject { ["type"] = new JsonArray { "string", "null" } },
								},
								["required"] = new JsonArray { "type", "message" },
							},
						},
						["required"] = new JsonArray { "error" },
					},
				},
			},
		};
	}

	static JsonObject Operation(ModelContextProtocol.Protocol.Tool tool)
	{
		var operation = new JsonObject
		{
			["operationId"] = tool.Name,
			["summary"] = tool.Title ?? tool.Name,
			["description"] = tool.Description ?? string.Empty,
			["tags"] = new JsonArray { Family(tool.Name) },
			// The wrapper is the protocol's, not ours: a client sends the TOOL's own arguments object
			// under `arguments`, exactly as tools/call does. A flat body would look friendlier and be a
			// second, server-side binding to keep in sync — the drift this document exists to prevent.
			["requestBody"] = new JsonObject
			{
				["required"] = true,
				["content"] = new JsonObject
				{
					["application/json"] = new JsonObject
					{
						["schema"] = new JsonObject
						{
							["type"] = "object",
							["properties"] = new JsonObject
							{
								["arguments"] = JsonNode.Parse(tool.InputSchema.GetRawText()),
							},
							["required"] = new JsonArray { "arguments" },
							["additionalProperties"] = false,
						},
					},
				},
			},
			["responses"] = new JsonObject
			{
				["200"] = new JsonObject
				{
					["description"] = "The tool result (`structuredContent` of the MCP response).",
					["content"] = new JsonObject
					{
						["application/json"] = new JsonObject
						{
							["schema"] = tool.OutputSchema is { } output
								? JsonNode.Parse(output.GetRawText())
								: new JsonObject { ["type"] = "object" },
						},
					},
				},
				["default"] = new JsonObject
				{
					["description"] = "Structured error envelope.",
					["content"] = new JsonObject
					{
						["application/json"] = new JsonObject
						{
							["schema"] = new JsonObject { ["$ref"] = "#/components/schemas/McpError" },
						},
					},
				},
			},
		};

		// Which key may call it — the same declaration the invocation gate and tools/list read, so the
		// document answers "can MY key do this" without a probe call.
		if (McpToolScopes.Declared.TryGetValue(tool.Name, out var requirement))
		{
			operation["x-scope-kind"] = requirement.Kind.ToString();
			operation["x-required-scopes"] = new JsonArray(
				[.. requirement.Scopes.Select(s => (JsonNode?)JsonValue.Create(s))]);
		}

		return operation;
	}

	// First underscore segment: tasks_upsert → tasks, llm_config_upsert → llm. Groups the surface in
	// Swagger UI the way the tool families already read to an agent.
	static string Family(string name) =>
		name.IndexOf('_', StringComparison.Ordinal) is var i and > 0 ? name[..i] : name;

	// UTF-8, no BOM, LF line endings, one trailing newline — so the committed file is a stable
	// artifact and a generation run on any OS produces the same bytes.
	public static void WriteFile(string path, Assembly assembly, JsonSerializerOptions serializerOptions, IServiceProvider services)
	{
		var document = Build(assembly, serializerOptions, services);
		// ReplaceLineEndings: Utf8JsonWriter's indented writer emits the PLATFORM newline, so a Windows
		// build would otherwise leave CRLF in a file .gitattributes forces to LF — every build would
		// then report the artifact as modified and the "diff means drift" contract would be noise.
		var json = (document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n")
			.ReplaceLineEndings("\n");
		var full = Path.GetFullPath(path);
		var dir = Path.GetDirectoryName(full);
		if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
		File.WriteAllText(full, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		Console.WriteLine($"wrote {full} ({document["paths"]?.AsObject().Count ?? 0} tools, {json.Length} bytes)");
	}
}
