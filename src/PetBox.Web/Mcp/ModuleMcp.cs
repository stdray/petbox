using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Features;

namespace PetBox.Web.Mcp;

// Shared guards + JSON helpers for the tasks.*/memory.*/session.* MCP tools.
// Mirrors the private AssertProject/AssertScope helpers in DataTools/LogTools,
// factored out so the three new tool classes don't each copy them. Claims
// ("project", "scopes") are set by ApiKeyAuthenticationHandler.
static class ModuleMcp
{
	// IProjectCatalog is resolved from the request's own DI container (same pattern
	// ApiKeyAuthenticationHandler uses for IApiKeyLookup) rather than added as a new parameter to
	// every one of the ~90 call sites across the *Tools.cs files — AssertProject/ResolveProject
	// keep their existing (http, projectKey) shape, they just became async (the sandbox
	// containment check — ProjectScope.AuthorizesAsync — is a DB read).
	public static async Task AssertProject(IHttpContextAccessor http, string projectKey, CancellationToken ct = default)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var catalog = ctx.RequestServices.GetRequiredService<IProjectCatalog>();
		switch (await ProjectScope.EvaluateAsync(ctx.User, projectKey, catalog, ct))
		{
			case ProjectAccess.SandboxContainment:
				throw new UnauthorizedAccessException(ProjectScope.SandboxDenialMessage(projectKey));
			case ProjectAccess.ClaimMismatch:
				throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{projectKey}'");
		}
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
	public static async Task<string> ResolveProject(IHttpContextAccessor http, string? projectKey, CancellationToken ct = default)
	{
		var ctx = http.HttpContext ?? throw new InvalidOperationException("No HttpContext");
		var catalog = ctx.RequestServices.GetRequiredService<IProjectCatalog>();
		var effective = string.IsNullOrWhiteSpace(projectKey) ? DefaultProjectOf(ctx.User) : projectKey;
		if (string.IsNullOrWhiteSpace(effective))
			throw new ArgumentException(
				"projectKey is required (the API key is not scoped to a single project — pass projectKey, " +
				"or set a default project on the key)");
		switch (await ProjectScope.EvaluateAsync(ctx.User, effective, catalog, ct))
		{
			case ProjectAccess.SandboxContainment:
				throw new UnauthorizedAccessException(ProjectScope.SandboxDenialMessage(effective));
			case ProjectAccess.ClaimMismatch:
				throw new UnauthorizedAccessException($"ApiKey is not scoped to project '{effective}'");
		}
		return effective;
	}

	// The project the key falls back to when the caller omits projectKey: its own claim, or —
	// for a cross-project key — the `project_default` claim (absent ⇒ null ⇒ ResolveProject throws).
	//
	// Reads the PRINCIPAL, not the HttpContext, so the MCP filters (McpProjectDefaultFilter, whose
	// RequestContext carries a ClaimsPrincipal and no HttpContext) ask this exact question — the
	// injected argument and the advertised schema then agree with ResolveProject by construction
	// rather than by a re-implementation that can drift.
	//
	// The reading itself moved to PetBox.Core.Auth.CallerTenant when the endpoint-plane PEP needed the
	// same answer for a [TenantFrom(CallerDefault)] declaration; this stays as the MCP surface's name
	// for it. One reading, three callers (injection, existence check, both PEPs).
	public static string? DefaultProjectOf(ClaimsPrincipal? user) => CallerTenant.DefaultProjectOf(user);

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
	// { error: { type, message, traceId|detail } } result centrally for every tool. Tools keep concrete
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

	// ── write-call escape-inflation guidance (card mcp-write-degrades-silently-fix; number dropped
	// from the public text by work drop-size-number-from-tool-descriptions; the ABSOLUTE
	// Content-Length trigger itself retired by work escape-inflation-warning) ─────────────────
	// The truncation this warns about happens on the CALLING CLIENT (a tool-call JSON that
	// \uXXXX-escapes Cyrillic, or is simply large, gets cut before this server ever parses it —
	// PetBox does not enforce or even see that failure). An absolute Content-Length threshold used
	// to trigger this warning, but it measured the wrong thing: it fired on any big-but-honest
	// raw-UTF-8 call and stayed silent on a small-but-escaped one. The ReqBytes/ReqChars RATIO
	// (their per-character average) was rejected for the same reason — script mix makes the
	// ranges overlap: raw-UTF-8 CJK costs 3.0x/char, WORSE than \uXXXX-escaped Cyrillic at 30%
	// non-ASCII (2.5x/char) — no threshold on that ratio can separate the two classes (card
	// escape-inflation-warning).
	//
	// What separates them cleanly is not an average but a MEASUREMENT of the request body itself:
	//
	//     inflation = <bytes this body spent on the wire> / <bytes the SAME body needs as raw UTF-8>
	//
	// Both numbers describe the WHOLE request, envelope included (McpWireBodyMeasurementMiddleware
	// scans the body as it streams past — see JsonEscapeInflationScanner for why a scan and not a
	// reparse). That "same body" is the load-bearing word. The first cut of this detector divided
	// Request.ContentLength (the whole request) by the reserialized tool ARGUMENTS (a part of it),
	// so the ~178-byte JSON-RPC envelope sat in the numerator only: every call carrying under ~356
	// bytes of arguments measured (X + 178) / X >= 1.5 and got warned, pure ASCII included. Now the
	// envelope appears identically on both sides and cancels.
	//
	// inflation ≈ 1.0 is a raw-UTF-8 client — nothing to say, on ANY size. At or above
	// EscapeInflationWarningThreshold the client is spending real bytes on \uXXXX.
	internal const string WireBodyMeasurementItemKey = "petbox.mcp.wire_body_measurement";

	// 1.5x, unchanged from the first cut but now meaning something different — and something
	// stricter. Measured over the WHOLE body it says: at least a third of everything this call put
	// on the wire ((1 - 1/1.5) = 33%) was pure escape overhead. That is well clear of noise (there
	// is none left to speak of: the scan is exact, not a reserialized approximation, and it never
	// claims a saving it cannot justify), and comfortably under what a Cyrillic-carrying call
	// actually measures end to end — the prod evidence on this card is 2.33x for a body whose
	// ASCII scaffolding already dilutes the per-character 3.0x tax.
	internal const double EscapeInflationWarningThreshold = 1.5;

	// Shared wording for every write verb that carries a body (memory_remember, memory_upsert,
	// tasks_upsert, comments_upsert, session_append, session_upsert) — one class of problem (work
	// write-verbs-size-limit-still-has-no-number / comments-upsert-size-guidance /
	// size-warning-not-wired-to-write-verbs / drop-size-number-from-tool-descriptions), one
	// sentence, so the guidance cannot drift to a different number — or a differently-worded
	// number — per tool. Deliberately carries NO byte figure (see the comments above for why);
	// SizeGuidanceTests pins that absence.
	public const string SizeGuidanceText =
		"Cyrillic bodies: send raw UTF-8, not \\uXXXX escapes (~2.8x the byte size, measured 2.74-2.88x). A " +
		"large JSON body can still be silently truncated by the calling client before this server ever sees " +
		"it — a client-side limit PetBox cannot prevent or name an exact number for. What PetBox DOES do: " +
		"measure escape-inflation on an applied write's wire bytes and return a `warning` naming the " +
		"multiplier when it crosses the threshold. Silence isn't proof of raw UTF-8 — a compressed or " +
		"repacked body also measures ~1.0 — only the warning is evidence. Split large batches into multiple calls.";

	// Point 4 of the card mcp-write-degrades-silently-fix: a write the server DID accept and apply
	// can still have paid the \uXXXX-escaping tax silently — it only fails once a slightly bigger
	// call crosses the client's truncation edge, and by then the caller has no warning to have
	// acted on. The server cannot tell an escaped payload from a raw one by READING it (the JSON
	// parser already decoded it by the time a tool method runs — the same boundary SizeGuidanceText
	// documents), but it CAN measure it, on the wire bytes themselves: what this body cost against
	// what the SAME body would cost with its non-ASCII spelled raw (WireBodyMeasurementItemKey,
	// published by McpWireBodyMeasurementMiddleware). Surfaced as a WARNING on an already-successful
	// write, never a refusal — the write already landed, and the point is to inform the NEXT call,
	// not punish this one.
	//
	// Null whenever there is nothing trustworthy to compare:
	//   * no measurement on this context — a call that did not come through POST /mcp (a direct
	//     tool invocation in a test, a future in-process transport) is unknown, never guessed at;
	//   * an empty body — nothing was measured, never divide by zero;
	//   * wire < raw — structurally impossible for this scanner (it only ever subtracts), kept as
	//     a belt against a future measurement source that could be lossy, e.g. transport
	//     compression counted after the fact.
	public static string? SizeWarningOrNull(IHttpContextAccessor http)
	{
		if (http.HttpContext is not { } ctx) return null;
		if (!ctx.Items.TryGetValue(WireBodyMeasurementItemKey, out var item) ||
			item is not JsonEscapeInflationScanner scanner)
			return null;

		var wire = scanner.WireBytes;
		var rawUtf8 = scanner.RawUtf8Bytes;
		if (wire <= 0 || rawUtf8 <= 0 || wire < rawUtf8) return null;

		var inflation = (double)wire / rawUtf8;
		if (inflation < EscapeInflationWarningThreshold) return null;

		return $"This call's request body measured {inflation:0.0}x what the same body costs as raw " +
			$"UTF-8 ({wire:N0} vs {rawUtf8:N0} bytes) — its non-ASCII is \\uXXXX-escaped. The write " +
			"applied — this is informational, not a refusal — but sending raw UTF-8 avoids paying that " +
			"multiplier on every call.";
	}
}
