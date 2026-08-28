namespace PetBox.Core.Settings;

// The rerank INPUT SIZE CAPS — how many characters of a candidate document (and of the query) are
// handed to the cross-encoder rerank pass (spec: search-rerank-in-loop, bug
// rerank-oversize-falls-through-both-legs). Today the SAME text that gets truncated before EMBEDDING
// (DuckDbSessionEpisodicIndex.EmbedCharCap = 2000) goes into rerank whole — sessions/memory/tasks each
// hand the reranker the full message content / Description+Body / Name+Body. A document that tokenizes
// past the local route's physical-batch ceiling (measured: exactly 8192 tokens per query+document PAIR,
// tied to `ubatch-size`) gets a deterministic HTTP 500 there; the cloud fallback has an even smaller
// ceiling (10240 tokens per WHOLE request), so the fallback is guaranteed to fail too and the precision
// pass is lost entirely (RRF degradation, honest but avoidable for the 99%+ of traffic that never needed
// it).
//
// DocumentChars default (6000) is chosen from the measured distribution in the bug card, not guessed:
// - Cyrillic tokenizes at roughly 1 token per 1.5-2 characters (owner's measured ratio), so 6000 chars
//   is ~3000-4000 tokens for the document alone — comfortably under the 8192-token PAIR ceiling with the
//   query still to add.
// - The successful-pair size distribution (n=1019, 2026-08-27) has p90=1874, p95=2267, p99=4383 tokens
//   PER PAIR (query+document combined) — so this cap sits at/above the natural p99 and does not touch
//   ordinary traffic; it only clips the pathological tail that was hitting 15936-52597 tokens.
// QueryChars default (2000) mirrors the existing EmbedCharCap precedent for the same reason: real search
// queries are short natural-language asks, so a 2000-char cap is pure defense against a degenerate
// caller, never ordinary traffic. Worst case combined (6000 + 2000 = 8000 chars) is still comfortably
// under the 8192-token ceiling even at the denser 1.5-chars/token ratio (~5333 tokens).
//
// Overridable System -> Workspace -> Project (spec: settings-uniform-override, deeper wins), the same
// cascade as RerankBudgetSettings. Read via:
//
//   resolver.GetAsync<RerankTruncationSettings>(Scope.Project, projectKey)
//
// then hand the result to PetBox.Core.Search.RerankInputTruncation.FromSettings(...).
public sealed record RerankTruncationSettings
{
	[Setting(TopLevel = Scope.System, Key = "search.rerank.truncate.documentChars",
		Description = "Max characters of a candidate document's text handed to the rerank cross-encoder. A document longer than this is truncated (not dropped) before the call, so an oversized document degrades ranking quality slightly rather than losing the entire precision pass to an upstream size-limit refusal.")]
	public int DocumentChars { get; init; } = 6000;

	[Setting(TopLevel = Scope.System, Key = "search.rerank.truncate.queryChars",
		Description = "Max characters of the search query text handed to the rerank cross-encoder. Defensive only — ordinary queries are far shorter.")]
	public int QueryChars { get; init; } = 2000;
}
