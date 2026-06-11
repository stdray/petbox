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
// delegate to the neutral contracts — ILlmClient (invoke) and ILlmRegistryAdmin (configure),
// which own validation (FluentValidation) and routing. It must NOT touch the router impl (a
// NetArchTest enforces dependence on PetBox.LlmRouter.Contract only). Scopes: llm:invoke
// (embed/rerank/chat) and llm:admin (read/write registry).
[McpServerToolType]
public static class LlmRouterTools
{
	// Web defaults (camelCase, case-insensitive); LlmCapability carries its own string
	// converter so routes read/write "embed"/"rerank"/"chat".
	static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	// The config_set payload: endpoints + routes (-> LlmRegistry) plus write-only api keys.
	public sealed record ConfigSetInput(
		IReadOnlyList<LlmEndpoint> Endpoints,
		IReadOnlyList<LlmRoute> Routes,
		Dictionary<string, string>? ApiKeys = null);

	[McpServerTool(Name = "llm.config_get", Title = "Get LLM router registry", ReadOnly = true, UseStructuredContent = true, OutputSchemaType = typeof(LlmRegistry))]
	[Description("Return the project's LLM router registry (endpoints + routes), WITHOUT secrets. Requires llm:admin.")]
	public static async Task<object> ConfigGetAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmRegistryAdmin admin,
		string projectKey, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmAdmin);
		return await admin.GetAsync(projectKey, ct);
	});

	[McpServerTool(Name = "llm.config_set", Title = "Set LLM router registry", UseStructuredContent = true, OutputSchemaType = typeof(LlmConfigSetResult))]
	[Description("""
		Replace the project's LLM router registry. Requires llm:admin.
		`config` is a JSON object:
		  { "endpoints": [ { "name", "baseUrl", "certThumbprint"?, "connectTimeoutMs"?, "requestTimeoutMs"? } ],
		    "routes":    [ { "capability": "embed|rerank|chat", "endpoint", "model", "priority"?, "tier"?, "thinking": "enabled|disabled"? } ],
		    "apiKeys":   { "<endpointName>": "<apiKey>" }?  // write-only, stored encrypted; omit to keep existing }
		Validated before save (unknown endpoint in a route, bad URL, etc. -> error).
		""")]
	public static async Task<object> ConfigSetAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmRegistryAdmin admin,
		string projectKey,
		[Description("JSON object { endpoints[], routes[], apiKeys? }")] JsonElement config,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmAdmin);

		var input = Deserialize<ConfigSetInput>(config)
			?? throw new ArgumentException("config must be a JSON object with endpoints + routes");
		var registry = new LlmRegistry(input.Endpoints ?? [], input.Routes ?? []);
		await admin.SetAsync(projectKey, registry, input.ApiKeys ?? new Dictionary<string, string>(), ct);
		return new LlmConfigSetResult(true, registry.Endpoints.Count, registry.Routes.Count);
	});

	[McpServerTool(Name = "llm.embed", Title = "Embed text via the router", UseStructuredContent = true, OutputSchemaType = typeof(EmbedResult))]
	[Description("""
		Embed inputs through the router's embed chain (primary -> fallback). `inputs` is a JSON
		array of strings. Optional `tier`. Returns { vectors, model, servedBy }. Requires llm:invoke.
		""")]
	public static async Task<object> EmbedAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmClient client,
		string projectKey,
		[Description("JSON array of strings")] JsonElement inputs,
		string? tier = null, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmInvoke);

		var texts = Deserialize<List<string>>(inputs) ?? throw new ArgumentException("inputs must be a JSON array of strings");
		return await client.EmbedAsync(projectKey, new EmbedRequest(texts, tier), ct);
	});

	[McpServerTool(Name = "llm.rerank", Title = "Rerank documents via the router", UseStructuredContent = true, OutputSchemaType = typeof(RerankResult))]
	[Description("""
		Rerank `documents` (JSON array of strings) against `query` through the rerank chain.
		Optional `topN`, `tier`. Returns { hits:[{index,score}], model, servedBy }. Requires llm:invoke.
		""")]
	public static async Task<object> RerankAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmClient client,
		string projectKey, string query,
		[Description("JSON array of document strings")] JsonElement documents,
		int? topN = null, string? tier = null, CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmInvoke);

		var docs = Deserialize<List<string>>(documents) ?? throw new ArgumentException("documents must be a JSON array of strings");
		return await client.RerankAsync(projectKey, new RerankRequest(query, docs, topN, tier), ct);
	});

	[McpServerTool(Name = "llm.chat", Title = "Chat / summarize via the router", UseStructuredContent = true, OutputSchemaType = typeof(ChatResult))]
	[Description("""
		Run a chat/summary completion through the chat chain. `messages` is a JSON array of
		{ role, content }. Optional `tier`, `temperature`, `maxTokens`. Returns { text, model,
		servedBy }. Requires llm:invoke.
		""")]
	public static async Task<object> ChatAsync(
		IHttpContextAccessor http, FeatureFlags features, ILlmClient client,
		string projectKey,
		[Description("JSON array of { role, content } messages")] JsonElement messages,
		string? tier = null, double? temperature = null, int? maxTokens = null,
		CancellationToken ct = default) => await ModuleMcp.GuardAsync(async () =>
	{
		ModuleMcp.AssertFeature(features, Feature.LlmRouter);
		ModuleMcp.AssertProject(http, projectKey);
		ModuleMcp.AssertScope(http, ApiKeyScopes.LlmInvoke);

		var msgs = Deserialize<List<ChatMessage>>(messages);
		if (msgs is null || msgs.Count == 0) throw new ArgumentException("messages must be a non-empty JSON array of { role, content }");
		return await client.ChatAsync(projectKey, new ChatRequest(msgs, tier, temperature, maxTokens), ct);
	});

	// Deserialize a tool argument into T. Handles the case where an MCP client double-encodes
	// the value as a JSON string (a stale-schema artifact) by parsing the string first.
	static T? Deserialize<T>(JsonElement el) =>
		el.ValueKind == JsonValueKind.String
			? JsonSerializer.Deserialize<T>(el.GetString() ?? "", Json)
			: el.Deserialize<T>(Json);
}
