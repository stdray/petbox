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
		var (vectors, served, route) = await RunChainAsync(projectKey, LlmCapability.Embed, request.Tier,
			(http, ep, key, route) => _upstream.EmbedAsync(http, ep.BaseUrl, key, route.Model, request.Inputs, ct), ct);
		var dim = vectors.Count > 0 ? vectors[0].Length : (int?)null;
		// THE decoupling: the provider was called with route.Model (above), but the identity that keys
		// the vector index is EmbedSpaceId when the route declares one — so a fallback provider whose
		// Model string differs still lands vectors in the SAME space as the primary. Null EmbedSpaceId
		// falls back to Model: the pre-existing index (keyed by the home model name) stays valid.
		return new EmbedResult(vectors, new ModelIdentity(route.EmbedSpaceId ?? route.Model, dim), served);
	}

	public async Task<RerankResult> RerankAsync(string projectKey, RerankRequest request, CancellationToken ct = default)
	{
		var (hits, served, route) = await RunChainAsync(projectKey, LlmCapability.Rerank, request.Tier,
			(http, ep, key, route) => _upstream.RerankAsync(http, ep.BaseUrl, key, route.Model, request.Query, request.Documents, request.TopN, ct), ct);
		// Rerank identity is the provider model, unchanged — EmbedSpaceId is embed-only.
		return new RerankResult(hits, new ModelIdentity(route.Model), served);
	}

	// Query-level model AFFINITY (spec: search-rerank-single-model). The correctness invariant is
	// ONE SEARCH QUERY = ONE MODEL FOR ALL ITS CHUNKS: when a query's candidate pool is chunked
	// across several rerank calls, every chunk MUST be scored by the same model — otherwise two
	// score scales land in one fused result set and NOTHING notices (the two-scales hole).
	//
	// Today a whole document list ships as a single POST (RerankAsync above), so that safety is a
	// property of the CALL FORM, not a guarantee: the moment a candidate pool exceeds one provider
	// call and chunking is introduced, per-chunk chain-walking (each chunk its own RerankAsync)
	// would route sibling chunks to different models on the FIRST fallback. This method is that
	// guarantee made STRUCTURAL. It resolves the chain ONCE and, per route in priority order, scores
	// EVERY chunk on THAT route's model. Fallback is WHOLE-QUERY only: a transient failure on ANY
	// chunk discards the route's partial work and replays the ENTIRE query on the NEXT route — never
	// a per-chunk "as it comes" mix, never a config-pin (which would kill home-preference + fallback).
	public async Task<RerankResult> RerankQueryAsync(string projectKey, RerankQueryRequest request, CancellationToken ct = default)
	{
		var resolved = await _resolver.ResolveAsync(projectKey, ct);
		var routes = resolved.Registry.Routes
			.Where(r => r.Capability == LlmCapability.Rerank && TierMatches(r.Tier, request.Tier))
			.OrderBy(r => r.Priority)
			.ToList();

		if (routes.Count == 0)
		{
			var reason = resolved.NoRouteMessage(LlmCapability.Rerank) + (request.Tier is null ? "" : $" (tier '{request.Tier}')");
			LogNoRoute(_log, LlmCapability.Rerank, projectKey, resolved.WorkspaceKey, request.Tier ?? "-",
				resolved.Level?.ToString() ?? "none", resolved.InheritanceBlocked, reason);
			throw new LlmRouterException(LlmCapability.Rerank, false, reason, null, noRoute: true);
		}

		var chunks = Chunk(request.Documents, request.ChunkSize);

		var attempt = 0;
		Exception? last = null;
		foreach (var route in routes)
		{
			var ep = resolved.Registry.Endpoints.FirstOrDefault(e => e.Name == route.Endpoint);
			if (ep is null)
			{
				LogUnknownEndpoint(_log, LlmCapability.Rerank, route.Endpoint);
				continue;
			}
			if (_breaker.IsOpen(ep.Name))
			{
				LogCircuitOpen(_log, LlmCapability.Rerank, ep.Name);
				continue;
			}

			attempt++;
			try
			{
				var http = _clients.Get(ep);
				var apiKey = resolved.ApiKeys.GetValueOrDefault(ep.Name);
				// Score EVERY chunk on THIS route's model before returning anything. A chunk that
				// throws aborts the whole route (the partial `merged` is dropped), so no result ever
				// carries hits from two models — the invariant is enforced by construction here, not
				// hoped for. TopN is deferred to the merge, so each chunk asks for all its hits.
				var merged = new List<RerankHit>(request.Documents.Count);
				foreach (var (offset, docs) in chunks)
				{
					var hits = await _upstream.RerankAsync(http, ep.BaseUrl, apiKey, route.Model, request.Query, docs, null, ct);
					foreach (var h in hits) merged.Add(new RerankHit(offset + h.Index, h.Score));
				}
				_breaker.RecordSuccess(ep.Name);
				LogServed(_log, LlmCapability.Rerank, ep.Name, route.Model, attempt);
				// One model scored the whole pool → the scores share one scale, so a global order and
				// TopN across chunks are now meaningful. Stable '>' keeps earlier hits on score ties.
				var ordered = merged.OrderByDescending(h => h.Score).ToList();
				if (request.TopN is { } n && n >= 0 && n < ordered.Count) ordered = ordered.Take(n).ToList();
				return new RerankResult(ordered, new ModelIdentity(route.Model), new ServedBy(ep.Name, route.Model, attempt, false));
			}
			catch (LlmUpstreamException ux) when (!ux.Transient)
			{
				LogNonTransient(_log, LlmCapability.Rerank, ep.Name, ux);
				throw new LlmRouterException(LlmCapability.Rerank, false, $"Rerank failed on '{ep.Name}': {ux.Message}", ux);
			}
			catch (LlmUpstreamException ux)
			{
				last = ux;
				_breaker.RecordFailure(ep.Name);
				// Whole-query fallback: the NEXT route replays ALL chunks from scratch. A 429 keeps its
				// own classified event (event 306), exactly as RunChainAsync.
				if (ux.RateLimited) LogRateLimited(_log, LlmCapability.Rerank, ep.Name, ux.Message);
				else LogTransient(_log, LlmCapability.Rerank, ep.Name, ux.Message);
			}
		}

		var rateLimited = (last as LlmUpstreamException)?.RateLimited ?? false;
		throw new LlmRouterException(LlmCapability.Rerank, true,
			$"all {routes.Count} Rerank provider(s) failed (last attempt {attempt})", last, rateLimited: rateLimited);
	}

	// Contiguous, index-preserving chunking of a candidate pool. A non-positive size or a pool that
	// already fits in one chunk yields a SINGLE chunk at offset 0 — the degenerate single-POST form,
	// so the affinity path is a superset of today's behavior, not a fork of it.
	static List<(int Offset, IReadOnlyList<string> Docs)> Chunk(IReadOnlyList<string> docs, int chunkSize)
	{
		var chunks = new List<(int Offset, IReadOnlyList<string> Docs)>();
		if (chunkSize <= 0 || docs.Count <= chunkSize)
		{
			chunks.Add((0, docs));
			return chunks;
		}
		for (var off = 0; off < docs.Count; off += chunkSize)
			chunks.Add((off, docs.Skip(off).Take(Math.Min(chunkSize, docs.Count - off)).ToList()));
		return chunks;
	}

	public async Task<ChatResult> ChatAsync(string projectKey, ChatRequest request, CancellationToken ct = default)
	{
		var (text, served, route) = await RunChainAsync(projectKey, LlmCapability.Chat, request.Tier,
			(http, ep, key, route) => _upstream.ChatAsync(http, ep.BaseUrl, key, route.Model, request.Messages, request.Temperature, request.MaxTokens, route.Thinking, ct), ct);
		// Chat identity is the provider model, unchanged — EmbedSpaceId is embed-only.
		return new ChatResult(text, new ModelIdentity(route.Model), served);
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

	// The shared fallback walk for all three capabilities. Returns the winning LlmRoute (not just its
	// Model) so each capability can derive its OWN identity from it: Embed keys the index by
	// EmbedSpaceId ?? Model, Chat/Rerank by Model. ServedBy still reports the concrete provider Model.
	async Task<(TRaw Raw, ServedBy Served, LlmRoute Route)> RunChainAsync<TRaw>(
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
				return (raw, new ServedBy(ep.Name, route.Model, attempt, false), route);
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
				// A 429 gets its OWN classified event (spec: search-degraded-provenance): the owner
				// must be able to ask "were there rate-limit refusals?" of log_query, and a distinct
				// EventId + the {Endpoint}/{Capability} it carries is what makes that answerable —
				// buried inside the generic transient event 304 it was not. Every 429 is logged here,
				// even one a later provider in the chain then rescues (the refusal still HAPPENED).
				if (ux.RateLimited) LogRateLimited(_log, cap, ep.Name, ux.Message);
				else LogTransient(_log, cap, ep.Name, ux.Message);
			}
		}

		// If the chain exhausted on a rate limit, say so: the reason the consumer reports
		// (embed-rate-limited) is the more useful one for a throttled route than a generic transient.
		var rateLimited = (last as LlmUpstreamException)?.RateLimited ?? false;
		throw new LlmRouterException(cap, true,
			$"all {routes.Count} {cap} provider(s) failed (last attempt {attempt})", last, rateLimited: rateLimited);
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

	// The queryable rate-limit event (spec: search-degraded-provenance) — a 429 refusal, split out
	// of 304 so `log_query` can answer "were there rate-limit refusals?" by EventId (306) or by the
	// {Endpoint}/{Capability} it carries, without an incident. Fires per refusal, even when a later
	// provider rescues the call.
	[LoggerMessage(EventId = 306, Level = LogLevel.Warning, Message = "llm {Capability} RATE-LIMITED on '{Endpoint}' (HTTP 429): {Message} — trying next")]
	static partial void LogRateLimited(ILogger logger, LlmCapability capability, string endpoint, string message);

	[LoggerMessage(EventId = 305, Level = LogLevel.Warning,
		Message = "llm {Capability}: NO ROUTE for project '{Project}' (workspace '{Workspace}', tier '{Tier}', resolved level {Level}, inheritance blocked: {InheritanceBlocked}) — {Reason}. The capability is dead here")]
	static partial void LogNoRoute(ILogger logger, LlmCapability capability, string project, string workspace,
		string tier, string level, bool inheritanceBlocked, string reason);
}
