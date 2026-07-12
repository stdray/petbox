using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PetBox.Web.Mcp;

// The key's default project, applied to the ~66 tools whose `projectKey` is a REQUIRED parameter.
//
// W1 gave a cross-project ("*") key an explicit DefaultProjectKey and made ModuleMcp.ResolveProject
// the single resolver — but that only helps the handful of tools whose projectKey is OPTIONAL. On a
// tool with a required `string projectKey`, a caller that HAS a resolvable project still had to spell
// it out on every call. Making the C# parameter optional everywhere was rejected: the input schema is
// per-SERVER, so an optional projectKey would LIE to a wildcard key that has no default at all.
//
// So the two legs of this filter do it per-CALLER instead:
//
//   leg 1 (tools/call) — inject the caller's default into `projectKey` when the caller omitted it
//                        and the tool's own schema says projectKey is REQUIRED;
//   leg 2 (tools/list) — advertise EXACTLY that: on the same tools, drop projectKey from the
//                        schema's `required` list and say what it defaults to.
//
// One predicate drives both legs (RequiresProjectKey × DefaultProjectOf), so the schema a key is
// served can never disagree with what the call path does for that key. Consequences that fall out
// of the schema gate, and are the reason it is the gate:
//   * apikey_create is EXCLUDED — its projectKey is `string?` (allProjects and projectKey are
//     mutually exclusive there); injecting would turn valid calls into errors.
//   * the optional-projectKey tools (memory_search/memory_remember/session_search/search_reindex)
//     are EXCLUDED — an ABSENT projectKey is meaningful cascade semantics for them, and they already
//     get the default via ModuleMcp.ResolveProject. The filter keeps its hands off.
// An explicitly-supplied projectKey ALWAYS wins — it is never overwritten.
//
// The risk this buys (a FORGOTTEN projectKey now silently routes to the default project instead of
// failing loudly) is contained by making every injection greppable: the tool span carries
// `petbox.arg.projectKey_injected=true` and the ToolCalls self-log an `Injected` property.
//
// FAIL-OPEN throughout: any error and the request passes through untouched (the tool then throws the
// same "projectKey is required" it always did).
static class McpProjectDefaultFilter
{
	public const string ProjectKeyArg = "projectKey";

	// The span tag / self-log marker. Same petbox.arg.* namespace the [LogArg] shapers use, but this
	// one is not a caller-supplied knob — it records that the SERVER supplied it.
	public const string InjectedTag = "petbox.arg.projectKey_injected";

	public static void Register(IMcpRequestFilterBuilder filters)
	{
		filters.AddCallToolFilter(next => (request, ct) =>
		{
			Inject(request);
			return next(request, ct);
		});

		filters.AddListToolsFilter(next => async (request, ct) =>
		{
			var result = await next(request, ct);
			try
			{
				if (ModuleMcp.DefaultProjectOf(request.User) is { } project)
					result.Tools = result.Tools.Select(t => Advertise(t, project)).ToList();
			}
			catch
			{
				// fail open — never break tools/list over a schema rewrite
			}
			return result;
		});
	}

	// ── leg 1: tools/call ──────────────────────────────────────────────────────────────────────

	static void Inject(RequestContext<CallToolRequestParams> request)
	{
		try
		{
			if (request.Params is not { } p) return;

			// BLANK ≡ ABSENT, before anything else looks at the argument. ModuleMcp.ResolveProject
			// already reads a blank projectKey as "omitted"; every other reader used to see a real ""
			// — and `ProjectScope.Authorizes("*", "")` said yes, so a `projectKey:""` from a wildcard
			// key walked into `ScopedDbFiles.PathFor("")` = a literal `tasks/.db`. Dropping the key
			// here gives blank ONE meaning on every tool: the resolver's job (inject the default
			// below, or the tool's own "projectKey is required").
			if (IsBlank(p.Arguments)) p.Arguments = Without(p.Arguments!, ProjectKeyArg);

			if (ModuleMcp.DefaultProjectOf(request.User) is not { } project) return;
			if (Supplied(p.Arguments)) return;                                  // explicit ALWAYS wins
			if (!RequiresProjectKey(request.Services, p.Name)) return;          // the load-bearing gate

			var args = p.Arguments is null
				? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
				: new Dictionary<string, JsonElement>(p.Arguments, StringComparer.Ordinal);
			args[ProjectKeyArg] = JsonSerializer.SerializeToElement(project);
			p.Arguments = args;

			// McpTracingFilter is the OUTERMOST call-tool filter (registered last), so the ambient
			// activity here IS its `mcp.tool <name>` span — the misroute marker lands on the tool
			// span, not on the enclosing HTTP request span.
			Activity.Current?.SetTag(InjectedTag, true);
		}
		catch
		{
			// fail open — a failure to inject degrades to the pre-W2 behavior (the tool throws)
		}
	}

	// "The caller supplied projectKey": the key is present with a real value. An explicit JSON null
	// is not a usable project — treat it as absent (binding would fail on it anyway) and inject.
	// (A blank string is gone by now — Inject strips it as ABSENT.)
	static bool Supplied(IDictionary<string, JsonElement>? args) =>
		args is not null
		&& args.TryGetValue(ProjectKeyArg, out var el)
		&& el.ValueKind != JsonValueKind.Null
		&& el.ValueKind != JsonValueKind.Undefined;

	// A present-but-blank projectKey ("" / "   ") — a project reference to nothing.
	static bool IsBlank(IDictionary<string, JsonElement>? args) =>
		args is not null
		&& args.TryGetValue(ProjectKeyArg, out var el)
		&& el.ValueKind == JsonValueKind.String
		&& string.IsNullOrWhiteSpace(el.GetString());

	static Dictionary<string, JsonElement> Without(IDictionary<string, JsonElement> args, string key)
	{
		var copy = new Dictionary<string, JsonElement>(args, StringComparer.Ordinal);
		copy.Remove(key);
		return copy;
	}

	// Does THIS tool's own input schema list projectKey as `required`? Read from the server's
	// canonical ToolCollection (the same source tool_describe reads) — the schema is generated from
	// the C# signature, so a `string projectKey` is required and a `string? projectKey = null` is not
	// (McpOutputSchema.NullableAware prunes nullable params from `required`). Reading the SCHEMA
	// rather than a hand-kept tool list means a new tool is covered the day it is written, and a
	// parameter that turns optional leaves coverage the day it changes.
	static bool RequiresProjectKey(IServiceProvider? services, string? tool) =>
		Schema(services, tool) is { } schema && RequiresProjectKey(schema);

	// Does this tool take a projectKey AT ALL (required or optional)? McpProjectExistsFilter asks it
	// to decide whether an ABSENT projectKey is a project reference (the tool will resolve the key's
	// default INSIDE itself — memory_remember & co.) or simply not one (whoami, project_list, …).
	// Same schema source as RequiresProjectKey, so the two predicates cannot drift apart.
	public static bool TakesProjectKey(IServiceProvider? services, string? tool) =>
		Schema(services, tool) is { } schema
		&& schema.ValueKind == JsonValueKind.Object
		&& schema.TryGetProperty("properties", out var properties)
		&& properties.ValueKind == JsonValueKind.Object
		&& properties.TryGetProperty(ProjectKeyArg, out _);

	static JsonElement? Schema(IServiceProvider? services, string? tool)
	{
		if (services is null || string.IsNullOrEmpty(tool)) return null;
		var collection = services.GetService<IOptions<McpServerOptions>>()?.Value.ToolCollection;
		if (collection is null || !collection.TryGetPrimitive(tool, out var t) || t is null) return null;
		return t.ProtocolTool.InputSchema;
	}

	public static bool RequiresProjectKey(JsonElement schema) =>
		schema.ValueKind == JsonValueKind.Object
		&& schema.TryGetProperty("required", out var required)
		&& required.ValueKind == JsonValueKind.Array
		&& required.EnumerateArray().Any(e => e.ValueKind == JsonValueKind.String && e.GetString() == ProjectKeyArg);

	// ── leg 2: tools/list ──────────────────────────────────────────────────────────────────────

	// The per-caller schema. A tool whose projectKey is required is served to a key WITH a resolvable
	// project as a CLONE whose projectKey is optional and says so; every other tool is served as-is
	// (reference-equal — the canonical ToolCollection is never mutated, and a key with no default
	// still sees the honest "projectKey is required").
	public static Tool Advertise(Tool tool, string project)
	{
		if (!RequiresProjectKey(tool.InputSchema)) return tool;
		if (JsonNode.Parse(tool.InputSchema.GetRawText()) is not JsonObject schema) return tool;

		if (schema["required"] is JsonArray required)
		{
			for (var i = required.Count - 1; i >= 0; i--)
				if (required[i]?.GetValue<string>() == ProjectKeyArg)
					required.RemoveAt(i);
			if (required.Count == 0) schema.Remove("required");
		}

		if (schema["properties"] is JsonObject properties && properties[ProjectKeyArg] is JsonObject prop)
		{
			var description = prop["description"]?.GetValue<string>();
			prop["description"] = Note(description, project);
		}

		return new Tool
		{
			Name = tool.Name,
			Title = tool.Title,
			Description = tool.Description,
			InputSchema = JsonSerializer.SerializeToElement(schema),
			OutputSchema = tool.OutputSchema,
			Annotations = tool.Annotations,
			Icons = tool.Icons,
			Meta = tool.Meta,
		};
	}

	static string Note(string? description, string project)
	{
		var note = $"Optional for THIS key — defaults to `{project}` when omitted.";
		return string.IsNullOrWhiteSpace(description) ? note : $"{description.TrimEnd()} {note}";
	}
}
