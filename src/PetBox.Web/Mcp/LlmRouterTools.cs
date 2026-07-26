using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Http;
using PetBox.Core.Auth;
using PetBox.Core.Features;
using PetBox.LlmRouter.Contract;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// MCP surface for the LLM router. A THIN adapter: assert feature/project/scope, DESERIALIZE
// the JSON argument straight into typed contract records (no hand-rolled field walking), and
// delegate to the neutral contracts — ILlmClient (invoke) and ILlmRegistryEditor (configure),
// which own validation (FluentValidation) and routing. It must NOT touch the router impl (a
// NetArchTest enforces dependence on PetBox.LlmRouter.Contract only). Scopes: llm:invoke
// (embed/rerank/chat) and llm:admin (read/write registry).
//
// llm_config_* used to write ILlmRegistryAdmin (the old ConfigBindings store), which the router
// stopped reading at the flip — an upsert reported success and changed nothing the runtime could
// see. They now edit the LEVELLED registry the router resolves through.
//
// Both config results now also name their LEVEL (llm-l5 item 5, decided by work
// llm-config-get-level-derivation-trap). The level is derived from the project's WORKSPACE, so
// "the project's own level" — what these descriptions used to promise — is a phrase with no
// referent: `smoke`, the sandbox project, reads and writes the live `System:$` byte for byte like
// `$system` does, and an operator who trusted the old wording was one call from editing production.
// Naming the actual container in the answer is the contract memory already keeps (memory_search
// labels every row with its scope; memory_remember returns the container it wrote into) — this is
// that model applied here, not a new nomenclature.
// TENANT DECLARATION (spec authz-scope-declaration): the `projectKey` ARGUMENT, all five verbs.
//
// It declares WHERE THE TENANT COMES FROM, and nothing more — deliberately, because on llm_config_*
// the projectKey does NOT bound what the write reaches. The registry is LEVELLED and the level is
// derived from the project's WORKSPACE, which is why the descriptions above stopped promising a
// per-project level ("no projectKey isolates this write"). The tenant check answers "may this caller
// act as THIS project?"; which level that project resolves to is the router's own question and stays
// exactly where it is. Declaring a tenant is not a claim that the blast radius equals it.
//
// Coverage was already complete (five AssertProject calls, same ProjectScope), so no allow/deny
// outcome moves; the five calls come out here.
[McpServerToolType]
[TenantFrom(TenantSource.Argument, "projectKey")]
public static class LlmRouterTools
{
	// Web defaults: camelCase PROPERTY names, case-insensitive property matching. Enum VALUES are a
	// separate axis — LlmCapability/LlmThinking carry a JsonStringEnumConverter with no naming
	// policy, so they WRITE the declared member name ("Embed" / "Disabled") and READ any casing
	// (Enum.TryParse with ignoreCase). That asymmetry is why llm_config_get's Capitalized output
	// feeds back into llm_config_upsert unchanged; LlmRegistryJsonTests pins both directions.
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	// The config_upsert payload. Endpoints/Routes are NULLABLE and that is the contract, not an
	// accident of deserialization: a property the caller omitted arrives here as null and means
	// "keep what is stored", while an empty array means "clear it". Both used to collapse to `[]`,
	// which is how an upsert that named only routes wiped every endpoint and its api key.
	public sealed record ConfigSetInput(
		IReadOnlyList<LlmEndpoint>? Endpoints = null,
		IReadOnlyList<LlmRoute>? Routes = null,
		Dictionary<string, string>? ApiKeys = null);

	[McpServerTool(Name = "llm_config_get", Title = "Get LLM router registry", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(LlmConfigGetResult))]
	[Description("""
		Return the LLM router registry DECLARED at the level THIS PROJECT READS AND WRITES (endpoints
		+ routes), WITHOUT secrets, plus `version` (the CAS baseline for llm_config_upsert) and
		`level` — the level you are actually looking at. Requires llm:admin.
		THE LEVEL COMES FROM THE PROJECT'S WORKSPACE, NOT FROM THE PROJECT. There is no per-project
		level: every project of workspace `$system` reads and writes `System:$`, the live registry
		every other workspace inherits. Any other workspace resolves to `Workspace:<workspaceKey>`,
		shared by every project in it — a workspace that declares nothing INHERITS `System:$` for
		reads and is read-only here. Read `level` before you write: a projectKey that looks isolated
		is not evidence that the level is. The sandbox project `smoke` was itself the cautionary
		case, and it is why it now has a workspace of its own (M048).
		`version` 0 means THIS LEVEL declares nothing yet — NOT that the project has no registry.
		In that case `servedBy` names the level actually serving it, and declaring even one row here
		SHADOWS that inherited level WHOLE (levels resolve atomically, they never merge) for every
		project of this workspace.
		""")]
	public static async Task<LlmConfigGetResult> ConfigGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmRegistryEditor registry,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmAdmin);
		var declared = await registry.GetDeclaredAsync(projectKey, ct);
		return new LlmConfigGetResult(
			declared.Registry.Endpoints, declared.Registry.Routes, declared.Version,
			declared.Level, declared.ServedBy);
	}

	[McpServerTool(Name = "llm_config_upsert", Title = "Upsert LLM router registry", UseStructuredContent = true, OutputSchemaType = typeof(LlmConfigSetResult))]
	[Description("""
		PATCH the LLM router registry AT THE LEVEL THIS PROJECT WRITES TO, with optimistic
		concurrency. Requires llm:admin.
		WHERE THIS WRITE LANDS — read it before anything else. The target level is derived from the
		project's WORKSPACE, not from the project: every project of workspace `$system`, the sandbox
		project `smoke` INCLUDED, writes `System:$` — the live registry every other workspace
		inherits. Any other workspace writes `Workspace:<workspaceKey>`, shared by all its projects.
		There is no per-project level, so no projectKey isolates this write. Call llm_config_get
		first and read its `level` (this call returns `level` too, but by then it is written).
		Writing to a level that currently declares nothing (`version` 0, `servedBy` set) does not
		"add" to what serves the project: the new level SHADOWS the inherited one WHOLE — routes and
		api keys gone — for every project of that workspace.
		WRITE SEMANTICS, in full — this edits the LIVE router configuration and there is no undo:
		  * An OMITTED top-level part stays UNCHANGED. Omit `endpoints` and the stored endpoints
		    (and their api keys) are kept; omit `routes` and the stored routes are kept, ids and all.
		  * A part you DO send REPLACES that whole list — there is no per-item merge. To drop one
		    route, send the routes you want to keep; to clear a part entirely, send it as [].
		  * `version` is the CAS baseline from your last llm_config_get (0 = this level declares
		    nothing yet). A baseline that is not the level's current version REFUSES the whole call
		    and writes NOTHING — re-read with llm_config_get and resubmit. This is what makes a
		    concurrent edit a conflict instead of a silent overwrite.
		`config` is a JSON object:
		  { "endpoints": [ { "name", "baseUrl", "certThumbprint"?, "connectTimeoutMs"?, "requestTimeoutMs"? } ]?,  // omit = keep existing, [] = clear
		    "routes":    [ { "capability": "embed|rerank|chat", "endpoint", "model", "priority"?, "tier"?, "thinking": "enabled|disabled"?, "embedSpaceId"? } ]?,  // omit = keep existing, [] = clear
		    // embedSpaceId (embed routes only): the canonical vector-index key. Omit/null = use model.
		    // Give two embed routes the SAME embedSpaceId to make their vectors share one index space.
		    "apiKeys":   { "<endpointName>": "<apiKey>" }?  // write-only, stored encrypted; an endpoint absent here keeps its stored key }
		`capability` and `thinking` are CASE-INSENSITIVE on input: "embed" and "Embed" both parse, as
		do "disabled" and "Disabled". llm_config_get emits them Capitalized ("Embed" / "Disabled"),
		so its output can be edited and fed straight back — read-modify-write is the intended and
		safe cycle, and it does not need case conversion in between.
		The MERGED registry (kept parts + sent parts) is validated as a whole before anything is
		written — an unknown endpoint in a route, a bad URL, etc. is an error and nothing changes.
		Returns { ok, endpoints, routes, version, level } — `version` is the new baseline for your
		next upsert, `level` is the level this call actually wrote.
		""")]
	public static async Task<LlmConfigSetResult> ConfigUpsertAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmRegistryEditor registry,
		string projectKey,
		[McpJsonShape("object")]
		[Description("JSON object { endpoints[]?, routes[]?, apiKeys? } — an omitted part is KEPT, a sent part REPLACES that whole list, [] clears it.")] JsonElement config,
		[Description("CAS baseline: the `version` from your last llm_config_get (0 = this level declares nothing yet). A stale baseline refuses the whole call.")] long version = 0,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmAdmin);

		var input = Deserialize<ConfigSetInput>(config)
			?? throw new ArgumentException("config must be a JSON object with endpoints and/or routes");
		// The level is still WRITTEN whole — it resolves atomically, so a half-declared one is a
		// registry with routes pointing at keyless endpoints. What changed is where the "whole" comes
		// from: the parts the caller did not send are read back from the stored level instead of
		// being silently replaced with nothing.
		var written = await registry.PatchAsync(
			projectKey, input.Endpoints, input.Routes,
			input.ApiKeys ?? new Dictionary<string, string>(), version, ct);
		return new LlmConfigSetResult(
			true, written.Registry.Endpoints.Count, written.Registry.Routes.Count, written.Version,
			written.Level);
	}

	[McpServerTool(Name = "llm_embed", Title = "Embed text via the router", UseStructuredContent = true, OutputSchemaType = typeof(EmbedResult))]
	[Description("""
		Embed inputs through the router's embed chain (primary -> fallback). `inputs` is a JSON
		array of strings. Optional `tier`. Returns { vectors, model, servedBy }. Requires llm:invoke.
		The server imposes no count/size cap on `inputs` — the binding constraint is provider/router
		latency, not this endpoint; a large batch just takes longer, it isn't rejected or truncated.
		""")]
	public static async Task<EmbedResult> EmbedAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmClient client,
		string projectKey,
		[McpJsonShape("array")]
		[Description("JSON array of strings")] JsonElement inputs,
		string? tier = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmInvoke);

		var texts = Deserialize<List<string>>(inputs) ?? throw new ArgumentException("inputs must be a JSON array of strings");
		return await client.EmbedAsync(projectKey, new EmbedRequest(texts, tier), ct);
	}

	[McpServerTool(Name = "llm_rerank", Title = "Rerank documents via the router", UseStructuredContent = true, OutputSchemaType = typeof(RerankResult))]
	[Description("""
		Rerank `documents` (JSON array of strings) against `query` through the rerank chain.
		Optional `topN`, `tier`. Returns { hits:[{index,score}], model, servedBy }. Requires llm:invoke.
		No server-side document-count cap — the binding constraint is latency, set by the
		provider/router, not this endpoint. Measured on qwen3-rerank-0.6b, one warm production
		route (2026-07-18): n=100 ~0.95s, n=500 3.4-4.0s, n=750 4.9-5.5s, n=1000 6.4-7.1s, n=5000
		~43s, n=8000 ~68s (~6.1ms/doc + ~0.33s base). A 5s budget breaks around n~700-720; a safe
		p95 target is ~500 candidates. This is one route only, not a guarantee — the openrouter
		fallback is unmeasured and may differ.
		""")]
	public static async Task<RerankResult> RerankAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmClient client,
		string projectKey, string query,
		[McpJsonShape("array")]
		[Description("JSON array of document strings")] JsonElement documents,
		int? topN = null, string? tier = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmInvoke);

		var docs = Deserialize<List<string>>(documents) ?? throw new ArgumentException("documents must be a JSON array of strings");
		return await client.RerankAsync(projectKey, new RerankRequest(query, docs, topN, tier), ct);
	}

	[McpServerTool(Name = "llm_chat", Title = "Chat / summarize via the router", UseStructuredContent = true, OutputSchemaType = typeof(ChatResult))]
	[Description("""
		Run a chat/summary completion through the chat chain. `messages` is a JSON array of
		{ role, content }. Optional `tier`, `temperature`, `maxTokens`. Returns { text, model,
		servedBy }. Requires llm:invoke. No server-side size cap on `messages` — the binding
		constraint is provider/router latency, not this endpoint; a large payload just takes
		longer, it isn't rejected or truncated.
		""")]
	public static async Task<ChatResult> ChatAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmClient client,
		string projectKey,
		[McpJsonShape("array")]
		[Description("JSON array of { role, content } messages")] JsonElement messages,
		string? tier = null, double? temperature = null, int? maxTokens = null,
		CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmInvoke);

		var msgs = Deserialize<List<ChatMessage>>(messages);
		if (msgs is null || msgs.Count == 0) throw new ArgumentException("messages must be a non-empty JSON array of { role, content }");
		return await client.ChatAsync(projectKey, new ChatRequest(msgs, tier, temperature, maxTokens), ct);
	}

	// Deserialize a tool argument into T. Handles the case where an MCP client double-encodes
	// the value as a JSON string (a stale-schema artifact) by parsing the string first.
	static T? Deserialize<T>(JsonElement el) =>
		el.ValueKind == JsonValueKind.String
			? JsonSerializer.Deserialize<T>(el.GetString() ?? "", Json)
			: el.Deserialize<T>(Json);
}
