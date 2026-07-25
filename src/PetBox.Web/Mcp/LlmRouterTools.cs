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
// see. They now edit the LEVELLED registry the router resolves through. The SHAPE is unchanged:
// llm_config_get still returns a plain LlmRegistry (the one DECLARED at the project's own level).
// Adding level/inherited/owner to it is a breaking contract change and waits on the owner (llm-l5
// item 5).
[McpServerToolType]
public static class LlmRouterTools
{
	// Web defaults (camelCase, case-insensitive); LlmCapability carries its own string
	// converter so routes read/write "embed"/"rerank"/"chat".
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
		Return the LLM router registry DECLARED at this project's own level (endpoints + routes),
		WITHOUT secrets, plus `version` — the CAS baseline for llm_config_upsert. Inherited levels
		are not shown; version 0 means this level declares nothing yet. Requires llm:admin.
		""")]
	public static async Task<LlmConfigGetResult> ConfigGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmRegistryEditor registry,
		string projectKey, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmAdmin);
		var declared = await registry.GetDeclaredAsync(projectKey, ct);
		return new LlmConfigGetResult(declared.Registry.Endpoints, declared.Registry.Routes, declared.Version);
	}

	[McpServerTool(Name = "llm_config_upsert", Title = "Upsert LLM router registry", UseStructuredContent = true, OutputSchemaType = typeof(LlmConfigSetResult))]
	[Description("""
		PATCH the project's LLM router registry, with optimistic concurrency. Requires llm:admin.
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
		The MERGED registry (kept parts + sent parts) is validated as a whole before anything is
		written — an unknown endpoint in a route, a bad URL, etc. is an error and nothing changes.
		Returns { ok, endpoints, routes, version } where `version` is the new baseline for your next
		upsert.
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
		await ModuleMcp.AssertProject(http, projectKey, ct);
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
			true, written.Registry.Endpoints.Count, written.Registry.Routes.Count, written.Version);
	}

	[McpServerTool(Name = "llm_embed", Title = "Embed text via the router", UseStructuredContent = true, OutputSchemaType = typeof(EmbedResult))]
	[Description("""
		Embed inputs through the router's embed chain (primary -> fallback). `inputs` is a JSON
		array of strings. Optional `tier`. Returns { vectors, model, servedBy }. Requires llm:invoke.
		""")]
	public static async Task<EmbedResult> EmbedAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmClient client,
		string projectKey,
		[McpJsonShape("array")]
		[Description("JSON array of strings")] JsonElement inputs,
		string? tier = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmInvoke);

		var texts = Deserialize<List<string>>(inputs) ?? throw new ArgumentException("inputs must be a JSON array of strings");
		return await client.EmbedAsync(projectKey, new EmbedRequest(texts, tier), ct);
	}

	[McpServerTool(Name = "llm_rerank", Title = "Rerank documents via the router", UseStructuredContent = true, OutputSchemaType = typeof(RerankResult))]
	[Description("""
		Rerank `documents` (JSON array of strings) against `query` through the rerank chain.
		Optional `topN`, `tier`. Returns { hits:[{index,score}], model, servedBy }. Requires llm:invoke.
		""")]
	public static async Task<RerankResult> RerankAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmClient client,
		string projectKey, string query,
		[McpJsonShape("array")]
		[Description("JSON array of document strings")] JsonElement documents,
		int? topN = null, string? tier = null, CancellationToken ct = default)
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		await ModuleMcp.AssertProject(http, projectKey, ct);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmInvoke);

		var docs = Deserialize<List<string>>(documents) ?? throw new ArgumentException("documents must be a JSON array of strings");
		return await client.RerankAsync(projectKey, new RerankRequest(query, docs, topN, tier), ct);
	}

	[McpServerTool(Name = "llm_chat", Title = "Chat / summarize via the router", UseStructuredContent = true, OutputSchemaType = typeof(ChatResult))]
	[Description("""
		Run a chat/summary completion through the chat chain. `messages` is a JSON array of
		{ role, content }. Optional `tier`, `temperature`, `maxTokens`. Returns { text, model,
		servedBy }. Requires llm:invoke.
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
		await ModuleMcp.AssertProject(http, projectKey, ct);
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
