namespace PetBox.Core.Search;

// THE ONE place the lexical leg's column weights live (spec: search-doc-model-title-weights).
//
// bm25 in the лексическая нога weights the document's fields in DESCENDING importance
// Key > Title > Tags > Body — one weight default shared across ALL families (tasks, memory,
// session), because the document model is generic. Before this, fts5 bm25 ran with NO column
// weights at all (every field weight = 1.0).
//
// ── PRE-EVAL DEFAULTS — ORIENTATION, NOT FINAL ──────────────────────────────────────────────
// These numbers (Key 3 / Title 2 / Tags 2 / Body 1) are a reasoned STARTING orientation, not a
// tuned/validated result. The real weights are meant to come from an eval on a real query log
// (intake `search-ranking-eval-infra`); building that eval is SEPARATE later work and the
// mechanism ships NOW without blocking on it. When the eval runs, RE-TUNE here — this is the one
// file to touch. Do NOT cite these as measured.
//
// Low-stakes to ship as defaults: the cross-encoder reranker is now in the read loop (spec:
// search-rerank-in-loop), so a bm25 weighting affects candidate RECALL only (which docs enter the
// reranked pool), NOT the final ordering the user sees. A titled entity landing a few slots higher
// in the candidate union is the whole intended effect.
//
// These are `const` on purpose: fts5's bm25() weight arguments have to be LITERALS the SQL
// builder can spread at translation time (linq2db's Sql.Spread rejects a runtime array), and a
// `const` reference is inlined by the compiler into exactly such a literal. The POSITIONAL wiring
// — one weight per declared search_fts column, UNINDEXED address columns included — is spelled at
// the single lexical call site (SqliteFtsIndex.SearchAsync), which is the only place bm25 runs;
// keep that call's column order in lockstep with the schema the latest migration produces
// (tasks M020_SearchTitleColumn / memory M013_SearchTitleColumn):
//   [Scope, Type, Id, Text, Tags, Key, Title].
public static class FtsColumnWeights
{
	// Per-field weights, by name — the descending-importance ladder, in ONE place.
	public const double Body = 1.0;  // the entity's prose body (fts5 column `Text`)
	public const double Tags = 2.0;  // free tags
	public const double Title = 2.0; // the entity's title (its own column)
	public const double Key = 3.0;   // the entity's business key/slug — the most specific lexicon term

	// The neutral weight for the UNINDEXED address columns (Scope, Type, Id). They carry no tokens,
	// so their weight never contributes to a score — but bm25() still needs a positional argument
	// for each, or every following weight would shift onto the wrong column.
	public const double Unindexed = 1.0;
}
