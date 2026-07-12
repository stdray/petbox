using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Features;

namespace PetBox.Web.Mcp;

// Shared guards + JSON helpers for the tasks.*/memory.*/session.* MCP tools.
// Mirrors the private AssertProject/AssertScope helpers in DataTools/LogTools,
// factored out so the three new tool classes don't each copy them. Claims
// ("project", "scopes") are set by ApiKeyAuthenticationHandler.
static class ModuleMcp
{
	public static void AssertProject(IHttpContextAccessor http, string projectKey)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		if (!ProjectScope.Authorizes(claim, projectKey))
			throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{projectKey}'");
	}

	// THE single resolver for the effective projectKey on tools where it is OPTIONAL:
	//
	//     arg ?? (claim == "*" ? project_default claim : claim)
	//
	// An explicitly passed projectKey ALWAYS wins. Omitted, a project-scoped key defaults to
	// its own claim (it need not repeat it); a cross-project ("*") key — whose claim authorizes
	// every project but names none — falls back to the key's DefaultProjectKey, surfaced as the
	// `project_default` claim. Only when nothing resolves (a "*" key with no default) is this an
	// error. The result is always authorized against the claim.
	public static string ResolveProject(IHttpContextAccessor http, string? projectKey)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var claim = ctx.User.Claims.FirstOrDefault(c => c.Type == "project")?.Value;
		var effective = string.IsNullOrWhiteSpace(projectKey) ? DefaultProject(ctx, claim) : projectKey;
		if (string.IsNullOrWhiteSpace(effective))
			throw new ArgumentException(
				"projectKey is required (the API key is not scoped to a single project — pass projectKey, " +
				"or set a default project on the key)");
		if (!ProjectScope.Authorizes(claim, effective))
			throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{effective}'");
		return effective;
	}

	// The project the key falls back to when the caller omits projectKey: its own claim, or —
	// for a cross-project key — the `project_default` claim (absent ⇒ null ⇒ ResolveProject throws).
	static string? DefaultProject(HttpContext ctx, string? claim) => claim switch
	{
		null or "" => null,
		ProjectScope.AllProjects => Blank(ctx.User.Claims.FirstOrDefault(c => c.Type == ApiKeyAuthenticationHandler.DefaultProjectClaim)?.Value),
		var single => single,
	};

	static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	public static void AssertScope(IHttpContextAccessor http, string required)
	{
		if (!HasScope(http, required))
			throw new UnauthorizedAccessException($"ApiKey lacks required scope '{required}'");
	}

	// Non-throwing scope probe for OPTIONAL capabilities (e.g. tasks:approve elevating an
	// upsert to an approving actor). Reads the SESSION key's claims off the request
	// principal — the same source AssertScope enforces against.
	public static bool HasScope(IHttpContextAccessor http, string scope)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var scopes = ctx.User.Claims.FirstOrDefault(c => c.Type == "scopes")?.Value ?? "";
		var parts = scopes.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return parts.Contains(scope, StringComparer.Ordinal);
	}

	public static void AssertFeature(FeatureFlags features, Feature feature)
	{
		if (!features.IsEnabled(feature))
			throw new InvalidOperationException($"feature '{feature}' is disabled");
	}

	// NOTE: tool bodies no longer wrap themselves — they just throw on a failed Assert* (or
	// any deeper error), and McpErrorEnvelopeFilter converts the exception into the structured
	// { error: { type, message, detail } } result centrally for every tool. Tools keep concrete
	// Task<T> return types; the success schema is advertised via [McpServerTool(OutputSchemaType)].

	public static string? OptStr(JsonElement o, string name) =>
		o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
			? e.GetString()
			: null;

	public static string ReqStr(JsonElement o, string name)
	{
		var v = OptStr(o, name);
		if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException($"{name} is required");
		return v!;
	}

	public static long OptLong(JsonElement o, string name, long dflt) =>
		o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var e)
			&& e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)
			? v
			: dflt;

	// ── uniform body-length contract (spec bodylen-uniform-contract) ──────────────────
	// ONE meaning for `bodyLen` on every body-carrying MCP surface (search, echoes, node_get,
	// comments). A caller passes a nullable int and the four cases are identical everywhere:
	//   * omitted / null → the surface's DEFAULT (varies by surface, documented per tool:
	//                      DefaultSnippet for listings/search, FullBody for pointed reads,
	//                      NoBody for compact write-echoes);
	//   * FullBody (-1)  → the whole body;
	//   * NoBody  (0)    → no body (null → the serializer omits the field);
	//   * N > 0          → the first N chars, "…" appended when the body was cut.
	// Only the DEFAULT differs between surfaces; the explicit values (-1/0/N) mean the same
	// thing on every tool, so `bodyLen:0` can never read as "applied a full body" the way the
	// old split SliceBody(0=none)/SnippetBody(0=full) pair let it.
	public const int FullBody = -1;
	public const int NoBody = 0;

	// The default snippet length for listing/search surfaces when bodyLen is omitted — enough
	// to identify a row without dumping a wall of full bodies (fetch a full body with a pointed
	// read: tasks_node_get / memory_get, or pass bodyLen:-1).
	public const int DefaultSnippet = 240;

	// Resolve the uniform contract: `bodyLen` (the caller's nullable knob) against `dflt` (the
	// surface's documented default). Returns the body shaped for the wire (null = omit the field).
	public static string? Body(string? body, int? bodyLen, int dflt)
	{
		var len = bodyLen ?? dflt;              // omitted → the surface default
		if (string.IsNullOrEmpty(body)) return null;
		if (len < 0) return body;               // FullBody
		if (len == 0) return null;              // NoBody
		return body.Length <= len ? body : string.Concat(body.AsSpan(0, len), "…");
	}
}
