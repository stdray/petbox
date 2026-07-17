namespace PetBox.Core.Search;

// Provenance for a search response: which retrievers actually ran, whether the result is
// degraded (e.g. the semantic retriever was requested but embedding was unavailable, so only
// lexical ran) and — crucially — WHY (spec: search-provenance). A bare `degraded:true` is a
// dead end for a caller: it cannot tell "no embed route is configured for this project" (a
// CONFIG hole that will never fix itself) from "the embedder blipped" (retry later). The
// reason is a stable machine code (SearchDegradedReason), never free text, so it can be
// alerted on. Null when nothing degraded.
//
// `SemanticLag` closes the OTHER honesty hole (spec: search-semantic-lag): `Semantic:true`
// means "the vector leg ANSWERED", not "its coverage is COMPLETE". After a reindex/outage the
// async vectorization worker's cursor trails the data, so a fresh doc is simply not embedded
// yet — invisible behind a bare `semantic:true`. This is that trail as a NUMBER (the same
// SourceVersion−Cursor the drain reports as DrainResult.Lag), so a caller can tell a fully-drained
// leg (0) from one behind by N. Null when no semantic leg ran (lexical-only / no embedder /
// degraded — nothing answered, so there is no coverage to be behind on).
//
// `Reranked` is laid into the contract NOW even though the reranker slice (B5) is deferred: turning
// it on later must not be a contract change. Default false = "no rerank pass ran" — which today is
// ALWAYS the case, and stays an honest answer until the reranker is wired.
public readonly record struct SearchRetrievers(
	bool Lexical,
	bool Semantic,
	bool Degraded,
	string? DegradedReason = null,
	long? SemanticLag = null,
	bool Reranked = false);

// The stable, machine-readable degradation codes carried by SearchRetrievers.DegradedReason.
// These are a CONTRACT (clients/alerts match on them) — extend, never rename.
public static class SearchDegradedReason
{
	// No Embed route is configured for the project → semantic search is structurally dead
	// here, not blipping. The one that silently killed vector search outside $system.
	public const string EmbedNoRoute = "embed-no-route";

	// The embedding provider answered with a definitive (4xx) error: bad key, bad model,
	// bad request. Config-level, will not self-heal.
	public const string EmbedUpstream4xx = "embed-upstream-4xx";

	// Every embedding provider in the chain failed transiently (refused/timeout/5xx) — the one
	// degradation that may well fix itself on the next call. Rate-limit (429) refusals are split
	// OUT of this into EmbedRateLimited so they stay separately answerable.
	public const string EmbedTransient = "embed-transient";

	// The embedding route EXISTS but the whole chain refused by RATE LIMIT (HTTP 429). This is the
	// THIRD provenance category (spec: search-degraded-provenance): not a config hole
	// (embed-no-route, which is NOT a blip), not a generic transient blip — the route being
	// THROTTLED. Split out of embed-transient on the owner's requirement that "were there
	// rate-limit refusals?" be answerable post-hoc by log_query, not by an incident: this code
	// (plus the router's classified event 306) is what makes it queryable.
	public const string EmbedRateLimited = "embed-rate-limited";

	// Anything else an index threw while answering (SQL error, corrupt file, …).
	public const string IndexError = "index-error";

	// Maps an embedding failure to its code. The LLM-router exception types stay OUT of Core
	// (llm-consumer-decoupling) — the per-consumer embedder adapter passes the facts. A 429 is
	// itself a transient failure, so rateLimited is checked BEFORE transient (it is the more
	// specific of the two).
	public static string Embed(bool noRoute, bool transient, bool rateLimited = false) =>
		noRoute ? EmbedNoRoute
		: rateLimited ? EmbedRateLimited
		: transient ? EmbedTransient
		: EmbedUpstream4xx;
}

// An index failure that already KNOWS why it failed. Thrown by the embedder adapters at the
// consumer edge so SearchService can put a code (not a guess) into the response provenance.
// Everything else that escapes an index degrades as `index-error`.
public sealed class SearchDegradedException : Exception
{
	public string Reason { get; }

	public SearchDegradedException(string reason, string message, Exception? inner = null)
		: base(message, inner) => Reason = reason;
}
