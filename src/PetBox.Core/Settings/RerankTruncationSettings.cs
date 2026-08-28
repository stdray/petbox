namespace PetBox.Core.Settings;

// The rerank INPUT SIZE CAPS — how many characters of a candidate document (and of the query) are
// handed to the cross-encoder rerank pass (spec: search-rerank-in-loop, bug
// rerank-oversize-falls-through-both-legs). Today the SAME text that gets truncated before EMBEDDING
// (DuckDbSessionEpisodicIndex.EmbedCharCap = 2000) goes into rerank whole — sessions/memory/tasks each
// hand the reranker the full message content / Description+Body / Name+Body. A document that tokenizes
// past a rerank route's physical-batch ceiling gets a deterministic size refusal there.
//
// The ceilings are per query+document PAIR on every route measured (NOT per whole request — the bug
// card's original "cloud caps the whole request at 10240" claim is REFUTED by direct measurement
// against openrouter, card comment a85af1e9d92e444d974e520c32b5f1ef: a 60-document / ~18k-token-total
// batch passes fine on every route; only a single oversized PAIR fails). The owner has since raised
// home (`models.ini`, `[qwen3-rerank-0.6b]`, `ctx-size`/`batch-size`/`ubatch-size`) from 8192 to 10240
// to match the cloud fallback's nominal ceiling — but "nominal" isn't what a document must fit under:
// each route also spends some of that budget on its own per-request overhead (query + prompt
// template), and that overhead differs by route (card comment bede7926182f4d78bec1e6c6236f72af,
// measured 2026-08-27):
//
//   route     nominal ceiling   overhead (measured)   EFFECTIVE document budget
//   home      10240             ~75 tokens            ~10165
//   nemotron  10240             ~11 tokens             ~10229
//
// so the binding number for a cap that must survive the SMALLEST route is the smallest EFFECTIVE
// budget, ~10165 tokens — not the nominal 10240 either route advertises.
//
// DocumentChars default (10000) is sized against that ~10165-token effective floor, not guessed:
// - Cyrillic tokenizes at roughly 1 token per 1.5-2 characters (owner's measured ratio). At the
//   DENSEST end (1.5 chars/token, the conservative case for a cap): 10000 chars document -> ~6667
//   tokens, plus QueryChars=2000 chars -> ~1333 tokens, plus home's own ~75-token overhead = ~8075
//   tokens worst case — comfortably under home's ~10165-token effective budget (~2090 tokens / ~21%
//   margin), so a query this thick still clears the tightest route without touching the "ladder zone"
//   between home's (~10165) and nemotron's (~10229) effective budgets — a ~64-token window neither this
//   cap nor a caller can land inside from a document capped this far below both.
// - The successful-pair size distribution (n=1019, 2026-08-27) has p90=1874, p95=2267, p99=4383 tokens
//   PER PAIR (query+document combined) — 10000 chars alone is ~5000-6667 tokens BEFORE the query is even
//   added, i.e. already well above the natural p99 pair total, so this cap does not touch ordinary
//   traffic at all; it only clips the pathological tail that was hitting 15936-52597 tokens.
// QueryChars default (2000) mirrors the existing EmbedCharCap precedent for the same reason: real search
// queries are short natural-language asks, so a 2000-char cap is pure defense against a degenerate
// caller, never ordinary traffic. It is already the binding factor in the worst-case sum above.
//
// Overridable System -> Workspace -> Project (spec: settings-uniform-override, deeper wins), the same
// cascade as RerankBudgetSettings. Read via:
//
//   resolver.GetAsync<RerankTruncationSettings>(Scope.Project, projectKey)
//
// then hand the result to PetBox.Core.Search.RerankInputTruncation.FromSettings(...).
//
// A Project/Workspace override that raises these past the ~10165-token effective floor above re-opens
// the size-refusal path this bug is about — see RerankInputTruncation and the tail comment on the bug
// card for the residual regression that unlocks (CapabilityRouter stops its whole fallback chain on the
// FIRST non-transient refusal rather than trying a bigger-ceilinged route next).
public sealed record RerankTruncationSettings
{
	[Setting(TopLevel = Scope.System, Key = "search.rerank.truncate.documentChars",
		Description = "Max characters of a candidate document's text handed to the rerank cross-encoder. A document longer than this is truncated (not dropped) before the call, so an oversized document degrades ranking quality slightly rather than losing the entire precision pass to an upstream size-limit refusal. Sized against the SMALLEST route's effective (post-overhead) token budget, not the nominal config number — see the type-level comment.")]
	public int DocumentChars { get; init; } = 10000;

	[Setting(TopLevel = Scope.System, Key = "search.rerank.truncate.queryChars",
		Description = "Max characters of the search query text handed to the rerank cross-encoder. Defensive only — ordinary queries are far shorter.")]
	public int QueryChars { get; init; } = 2000;
}
