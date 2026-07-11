namespace PetBox.Core.Search;

// Provenance for a search response: which retrievers actually ran, whether the result is
// degraded (e.g. the semantic retriever was requested but embedding was unavailable, so only
// lexical ran) and — crucially — WHY (spec: search-provenance). A bare `degraded:true` is a
// dead end for a caller: it cannot tell "no embed route is configured for this project" (a
// CONFIG hole that will never fix itself) from "the embedder blipped" (retry later). The
// reason is a stable machine code (SearchDegradedReason), never free text, so it can be
// alerted on. Null when nothing degraded.
public readonly record struct SearchRetrievers(
	bool Lexical,
	bool Semantic,
	bool Degraded,
	string? DegradedReason = null);

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

	// Every embedding provider in the chain failed transiently (refused/timeout/5xx/429) —
	// the one degradation that may well fix itself on the next call.
	public const string EmbedTransient = "embed-transient";

	// Anything else an index threw while answering (SQL error, corrupt file, …).
	public const string IndexError = "index-error";

	// Maps an embedding failure to its code. The LLM-router exception types stay OUT of Core
	// (llm-consumer-decoupling) — the per-consumer embedder adapter passes the two facts.
	public static string Embed(bool noRoute, bool transient) =>
		noRoute ? EmbedNoRoute : transient ? EmbedTransient : EmbedUpstream4xx;
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
