using PetBox.Core.Contract;
using PetBox.Core.Data.Temporal;
using PetBox.Memory.Data;

namespace PetBox.Memory.Contract;

// Public request/response shapes for the Memory service. Adapters (MCP/UI) parse
// input into these and serialize the results; the service owns taxonomy parsing,
// tag normalization, FTS search and the temporal write path.

// One entry as submitted to UpsertAsync. Type is a raw string — the service validates
// the taxonomy. Tags is an ARRAY of tag strings (the memory surface speaks arrays, like
// tasks): null = omit (PATCH: keep the current set), [] = explicit clear, a non-empty
// list REPLACES the set. The service normalizes (trim/lowercase/dedup) and joins to the
// CSV storage form at the boundary (domain rules, one place).
public sealed record MemoryEntryInput
{
	public required string Key { get; init; }
	public long Version { get; init; }
	public string? Type { get; init; }
	public string? Description { get; init; }
	public string? Body { get; init; }

	// Point edit of the body instead of a full replace (spec/write-cost-follows-change): a list of
	// {old, new} substitutions applied IN ORDER against the CURRENT active row's body, resolved in
	// the service's read-merge — the same `cur` every other omitted field inherits from — so the
	// substitution rides the existing version watermark rather than a second, racier read.
	// Mutually exclusive with Body. Every failure (zero matches, more than one match, both fields
	// present) is a REFUSAL through the ordinary conflict channel, never a partial application.
	public IReadOnlyList<FragmentEdit>? Fragment { get; init; }

	// A body that ALREADY EXISTS as a file, carried by REFERENCE instead of retyped into this call
	// (spec/no-retransmission-of-existing-content). ALREADY RESOLVED by the adapter against the
	// CALLER's authority — see BodyRefResolution for why the lookup cannot happen down here — so
	// this service only decides what the verdict MEANS for the write.
	// Mutually exclusive with Body and with Fragment: all three are answers to "what is the new
	// text", and honouring any one over another would be a guess. Every refusal rides the ordinary
	// conflict channel, exactly as a fragment refusal does.
	public BodyRefResolution? BodyRef { get; init; }
	public IReadOnlyList<string>? Tags { get; init; }
	public string? Metadata { get; init; }
	public string? PrevKey { get; init; }
}

// A soft-delete request: close the active entry at Key (Version 0 = regardless).
public sealed record MemoryDelete(string Key, long Version);

// An active entry projected for read surfaces (Type stringified; Tags split from the CSV
// storage form into the array the surface speaks).
public sealed record MemoryEntryView(string Key, string Type, string Description, string Body, IReadOnlyList<string> Tags, long Version, string Metadata);

// The raw temporal upsert/delta result, ready for an adapter to serialize.
public sealed record MemoryUpsertOutcome(TemporalUpsertResult<MemoryEntry> Result);

// A hybrid search result: the fused hits plus provenance (which retrievers actually ran
// and whether the answer is degraded, e.g. semantic was requested but embedding was
// unavailable so only lexical ran). Adapters surface Retrievers so callers can tell a
// lexical-only fallback from a true hybrid answer.
public sealed record MemorySearchResult(IReadOnlyList<MemoryEntryView> Hits, PetBox.Core.Search.SearchRetrievers Retrievers);

// A scored, provenance-tagged hit from a SINGLE store's hybrid search (store-scoped
// SearchScoredAsync — spec search-fair-fusion). It RETAINS the re-ranking signals the plain
// MemoryEntryView drops, so a caller can run its OWN relevance policy over one store's raw pool:
//   Score           — the fused RRF relevance (rank-based, before any decay), the quantity a
//                     relevance floor compares against;
//   Updated         — the entry's freshness timestamp (feeds RecencyDecay.Weight);
//   LexicalConfirmed— true when the LEXICAL leg surfaced this hit; false for a SEMANTIC-ONLY hit
//                     (no lexical confirmation → a caller may floor it as noise). Always true in a
//                     listing / lexical-only pass (no semantic leg ran, so nothing to floor);
//   Vector          — the entry's embedding for MMR diversification (null without an embedder or
//                     before the store was vectorized → MMR silently degrades to identity).
public sealed record MemoryScoredHit(MemoryEntryView Entry, DateTime Updated, double Score, bool LexicalConfirmed, float[]? Vector);

// The result of a store-scoped scored search: the scored hits (fused order) plus the aggregate
// retriever provenance (which legs ran / degraded) — the same provenance MemorySearchResult carries.
public sealed record MemoryScoredSearchResult(IReadOnlyList<MemoryScoredHit> Hits, PetBox.Core.Search.SearchRetrievers Retrievers);

// ---- unified read (spec uniform-entity-verbs v2): list = search without a query ----

// Filter axes of the unified memory read. `Store` narrows to one store within the container
// (null = sweep every store except the sensitive ones — see MemoryService.SweepExcludedStores);
// `Type` is the taxonomy predicate (User|Feedback|Project|Reference). Both are predicates in
// BOTH modes (a filter never ranks).
public sealed record MemoryEntryFilter(string? Store = null, string? Type = null);

// Sort axes of the unified memory read. Relevance exists only WITH a query (the fused order);
// Created/Updated read the active revision's temporal columns. The no-query default is
// Updated desc (the freshest fact first — memory keys are opaque generated ids, so key order
// carries no meaning, and a PATCHed entry should resurface).
public enum MemorySortBy
{
	Relevance,
	Created,
	Updated,
}

// One selected entry labelled by its owning store (a container read sweeps stores, so rows
// may span them — the label keeps provenance visible, mirroring TaskSearchHit.Board). `Score`
// is the fused, freshness-blended relevance (query mode; 0 in a listing) — the adapter uses it
// to HONESTLY merge across scopes (project ⊕ workspace) so the best hit wins regardless of
// container, rather than the old greedy "project takes the limit, workspace gets the remainder".
// Retriever names how the hit surfaced in query mode ("lexical" = lexically confirmed,
// "semantic" = surfaced by the vector leg alone); null in listing mode (no relevance ran).
// `ScoreRaw` is the SAME fused relevance BEFORE the recency decay was blended in (0 in a
// listing). Delivery telemetry normalizes fit against it (kRel = ScoreRaw / top-1 ScoreRaw of the
// request): decay has already reordered the answer, so normalizing the DECAYED score would count
// freshness twice.
// `Created`/`Updated` are the entry's temporal columns (spec listing-tail-reachable): a listing
// caller that pages with a KeysetCursor needs the SAME sort-key value the service ordered by
// (Updated/Created) to seek past it on a later page — MemoryEntryView (the wire-facing projection)
// deliberately does not carry them, so they live here, on the richer service-layer hit, instead.
public sealed record MemoryEntryHit(
	string Store, MemoryEntryView Entry, double Score = 0, string? Retriever = null, double ScoreRaw = 0,
	DateTime Created = default, DateTime Updated = default);

// The rich per-family result of the unified read: the selected hits plus retriever
// provenance (null in listing mode, where no retriever runs).
public sealed record MemoryEntrySearchResult(
	IReadOnlyList<MemoryEntryHit> Hits,
	PetBox.Core.Search.SearchRetrievers? Retrievers,
	// Pool facts of a RELEVANCE selection (spec: result-set-pageable). PoolLimit is how deep ranking was
	// allowed to look; PoolBounded says the candidate union actually REACHED that depth, so the rows are
	// a PREFIX of the match set and not the whole of it. Both null/false in listing mode.
	int? PoolLimit = null,
	bool PoolBounded = false,
	// The change stamp of every store this query could see. Opaque except as a cursor fingerprint
	// ingredient — it is what makes a mid-walk write an error rather than a silent restart.
	string? DataVersion = null,
	// The identity of the ORDER this result was ranked in (spec: result-set-pageable). DataVersion
	// answers "did the data move"; this answers "did the RANKING move", which it can do with nothing
	// written — a rerank route recovering or failing between pages rebuilds the same rows in a
	// different sequence. Carried into the cursor so that becomes a loud refusal, not a splice.
	string? PoolOrderHash = null,
	// Whether this order came out of a FRESH cross-encoder pass rather than out of the stored pool
	// (KeysetCursor.AssertPoolAlive). A reranked order is a property of ONE PASS — measured, not assumed:
	// the same query over the same data comes back in a different order on the next call — so a cursor is
	// bound to the POOL that pass materialized, and this is what tells the adapter that pool is gone.
	// False for a cache hit, and false for any RRF order (Speed/degraded), which a rebuild DOES reproduce.
	// In a cascade it is the OR across the legs: the merged order is spliced from all of them, so one
	// rebuilt leg is a rebuilt answer.
	bool PoolRebuiltByRerank = false);
