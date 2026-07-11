using Microsoft.Extensions.Logging;
using PetBox.LlmRouter.Contract;
using PetBox.LlmRouter.Http;
using PetBox.LlmRouter.Registry;

namespace PetBox.LlmRouter.Routing;

// The ILlmClient implementation: for each capability it resolves the project's route chain,
// orders it by priority, and walks it (llm-fallback-chain). A transient failure
// (refused/timeout/5xx/429) records a breaker failure and moves to the next provider; a
// non-transient failure (4xx) is surfaced immediately without masking; a circuit-open
// endpoint is skipped without a connection (llm-fast-down). Every served call logs who
// answered (llm-observability) via source-generated LoggerMessage — the logger config, not
// in-code level checks, decides what is emitted. Scoped (depends on the scoped resolver).
//
// The registry it resolves through is the LEVELLED one in core.db (ILlmRegistryLevelResolver,
// spec: llm-registry-own-store), NOT the old ConfigBindings-backed LlmRegistryStore. That flip is
// what turns semantic search on for every project outside $system: a workspace with no registry of
// its own now INHERITS the system level instead of resolving to nothing. And because the resolver
// knows WHY a project has no routes, the no-route error says so — "the system registry is not
// inherited here" is a different sentence from "there is no route anywhere", and both reach the
// caller (degradedReason) and the log (event 305).
public sealed partial class CapabilityRouter : ILlmClient
{
	readonly ILlmRegistryLevelResolver _resolver;
	readonly CertPinningHttpClientProvider _clients;
	readonly IOpenAiCompatibleClient _upstream;
	readonly EndpointBreaker _breaker;
	readonly ILogger<CapabilityRouter> _log;

	public CapabilityRouter(
		ILlmRegistryLevelResolver resolver,
		CertPinningHttpClientProvider clients,
		IOpenAiCompatibleClient upstream,
		EndpointBreaker breaker,
		ILogger<CapabilityRouter> log)
	{
		_resolver = resolver;
		_clients = clients;
		_upstream = upstream;
		_breaker = breaker;
		_log = log;
	}

	public async Task<EmbedResult> EmbedAsync(string projectKey, EmbedRequest request, CancellationToken ct = default)
	{
		var (vectors, served, model) = await RunChainAsync(projectKey, LlmCapability.Embed, request.Tier,
			(http, ep, key, route) => _upstream.EmbedAsync(http, ep.BaseUrl, key, route.Model, request.Inputs, ct), ct);
		var dim = vectors.Count > 0 ? vectors[0].Length : (int?)null;
		return new EmbedResult(vectors, new ModelIdentity(model, dim), served);
	}

	public async Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default)
	{
		var (hits, served, model) = await RunChainAsync(projectKey, LlmCapability.Rerank, request.Tier,
			(http, ep, key, route) => _upstream.RerankAsync(http, ep.BaseUrl, key, route.Model, request.Query, request.Documents, request.TopN, ct), ct);
		return new RerankResult(hits, new ModelIdentity(model), served);
	}

	public async Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
	{
		var (text, served, model) = await RunChainAsync(projectKey, LlmCapability.Chat, request.Tier,
			(http, ep, key, route) => _upstream.ChatAsync(http, ep.BaseUrl, key, route.Model, request.Messages, request.Temperature, request.MaxTokens, route.Thinking, ct), ct);
		return new ChatResult(text, new ModelIdentity(model), served);
	}

	public async Task<bool> IsAvailableAsync(string projectKey, LlmCapability capability, CancellationToken ct = default)
	{
		var resolved = await _resolver.ResolveAsync(projectKey, ct);
		return resolved.Registry.Routes
			.Where(r => r.Capability == capability)
			.Any(r =>
			{
				var ep = resolved.Registry.Endpoints.FirstOrDefault(e => e.Name == r.Endpoint);
				return ep is not null && !_breaker.IsOpen(ep.Name);
			});
	}

	// The shared fallback walk for all three capabilities.
	async Task<(TRaw Raw, ServedBy Served, string Model)> RunChainAsync<TRaw>(
		string projectKey, LlmCapability cap, string? tier,
		Func<HttpClient, LlmEndpoint, string?, LlmRoute, Task<TRaw>> call,
		CancellationToken ct)
	{
		var resolved = await _resolver.ResolveAsync(projectKey, ct);
		var routes = resolved.Registry.Routes
			.Where(r => r.Capability == cap && TierMatches(r.Tier, tier))
			.OrderBy(r => r.Priority)
			.ToList();

		if (routes.Count == 0)
		{
			// The hole that killed semantic search everywhere outside $system and never said a
			// word: a project with no Embed route throws here on EVERY query, and the only thing
			// that ever escaped was a mute `degraded:true`. Say it out loud, with the project AND
			// the reason — the resolver distinguishes "nobody has a route" from "inheritance is
			// switched off here", and that distinction is the whole point of asking it.
			var reason = resolved.NoRouteMessage(cap) + (tier is null ? "" : $" (tier '{tier}')");
			LogNoRoute(_log, cap, projectKey, resolved.WorkspaceKey, tier ?? "-",
				resolved.Level?.ToString() ?? "none", resolved.InheritanceBlocked, reason);
			throw new LlmRouterException(cap, false, reason, null, noRoute: true);
		}

		var attempt = 0;
		Exception? last = null;
		foreach (var route in routes)
		{
			var ep = resolved.Registry.Endpoints.FirstOrDefault(e => e.Name == route.Endpoint);
			if (ep is null)
			{
				LogUnknownEndpoint(_log, cap, route.Endpoint);
				continue;
			}
			if (_breaker.IsOpen(ep.Name))
			{
				LogCircuitOpen(_log, cap, ep.Name);
				continue;
			}

			attempt++;
			try
			{
				var http = _clients.Get(ep);
				var apiKey = resolved.ApiKeys.GetValueOrDefault(ep.Name);
				var raw = await call(http, ep, apiKey, route);
				_breaker.RecordSuccess(ep.Name);
				LogServed(_log, cap, ep.Name, route.Model, attempt);
				return (raw, new ServedBy(ep.Name, route.Model, attempt, false), route.Model);
			}
			catch (LlmUpstreamException ux) when (!ux.Transient)
			{
				LogNonTransient(_log, cap, ep.Name, ux);
				throw new LlmRouterException(cap, false, $"{cap} failed on '{ep.Name}': {ux.Message}", ux);
			}
			catch (LlmUpstreamException ux)
			{
				last = ux;
				_breaker.RecordFailure(ep.Name);
				LogTransient(_log, cap, ep.Name, ux.Message);
			}
		}

		throw new LlmRouterException(cap, true,
			$"all {routes.Count} {cap} provider(s) failed (last attempt {attempt})", last);
	}

	// A route with no tier is the default and serves any requested tier; a tiered route only
	// serves its exact tier. Priority then decides order among the matches.
	static bool TierMatches(string? routeTier, string? requestTier) =>
		routeTier is null || string.Equals(routeTier, requestTier, StringComparison.OrdinalIgnoreCase);

	[LoggerMessage(EventId = 300, Level = LogLevel.Warning, Message = "llm {Capability}: route references unknown endpoint '{Endpoint}', skipping")]
	static partial void LogUnknownEndpoint(ILogger logger, LlmCapability capability, string endpoint);

	[LoggerMessage(EventId = 301, Level = LogLevel.Debug, Message = "llm {Capability}: endpoint '{Endpoint}' circuit open, skipping")]
	static partial void LogCircuitOpen(ILogger logger, LlmCapability capability, string endpoint);

	[LoggerMessage(EventId = 302, Level = LogLevel.Information, Message = "llm {Capability} served by {Endpoint}/{Model} (attempt {Attempt})")]
	static partial void LogServed(ILogger logger, LlmCapability capability, string endpoint, string model, int attempt);

	[LoggerMessage(EventId = 303, Level = LogLevel.Warning, Message = "llm {Capability} non-transient failure on '{Endpoint}'")]
	static partial void LogNonTransient(ILogger logger, LlmCapability capability, string endpoint, Exception ex);

	[LoggerMessage(EventId = 304, Level = LogLevel.Warning, Message = "llm {Capability} transient failure on '{Endpoint}': {Message} — trying next")]
	static partial void LogTransient(ILogger logger, LlmCapability capability, string endpoint, string message);

	[LoggerMessage(EventId = 305, Level = LogLevel.Warning,
		Message = "llm {Capability}: NO ROUTE for project '{Project}' (workspace '{Workspace}', tier '{Tier}', resolved level {Level}, inheritance blocked: {InheritanceBlocked}) — {Reason}. The capability is dead here")]
	static partial void LogNoRoute(ILogger logger, LlmCapability capability, string project, string workspace,
		string tier, string level, bool inheritanceBlocked, string reason);
}
