using JetBrains.Annotations;
using PetBox.Core.Search;

namespace PetBox.Core.Contract;

// The generic uniform READ contract (spec uniform-entity-verbs v2): every entity family
// exposes ONE read verb — search — where `list` is simply a search WITHOUT a query and
// `relevance` is a sort option that only exists WITH a query. The contract is a shared
// SHAPE, not a DI seam: modules implement ISearchService explicitly on their own service
// (no polymorphic dispatch), adapters (MCP tools) stay thin — parse params, call the
// service, budget + shape the response.

// One read request: `Query` = null/empty → deterministic listing; non-empty → relevance
// selection over the family's search machinery. `Filter` narrows the pool in BOTH modes
// (a filter is a predicate, never a ranking). `Sort` reorders the selected set — with a
// query the default is relevance (the fused order) and an explicit sort reorders WITHIN
// the selected candidates; without a query the family's deterministic default applies and
// sorting by relevance is an error. `Limit` caps the rows (0 = the family default);
// `BodyLen` slices row bodies to the first N chars (0 = full) — the response budget then
// measures the post-slice wire rows.
// `RankingMode` is the RANKING axis the caller chose (spec: search-ranking-mode-is-caller-choice) —
// Precision (the default) attempts the штатный cross-encoder rerank pass when a route is live and
// falls through to the honest RRF degradation otherwise; Speed short-circuits straight to RRF,
// never even constructing a reranker. The service layer (TasksService/MemoryService) only
// PROPAGATES this value — it never guesses one when a caller leaves it at the default. The DEFAULT
// itself is an EDGE decision: MCP verbs (tasks_search/memory_search/session_search) want Precision
// (an agent acts on the answer — a ranking mistake costs more than latency), UI search pages want
// Speed (a human skims a list — latency costs more) — so each edge sets it explicitly when it
// builds the request rather than leaning on this record's bare default.
public sealed record SearchRequest<TFilter, TSort>
{
	public string? Query { get; init; }
	public TFilter? Filter { get; init; }
	public (TSort By, bool Desc)? Sort { get; init; }
	public int Limit { get; init; }
	public int BodyLen { get; init; }
	public SearchRankingMode RankingMode { get; init; } = SearchRankingMode.Precision;

	// PAGING (spec: result-set-pageable). When true the service returns the WHOLE ranked pool instead
	// of its first `Limit` rows, because the caller intends to seek into it with a keyset cursor and
	// slice its own page — the same division of labour a LISTING already uses, where the adapter owns
	// the cursor and the service owns the order.
	//
	// It is a separate flag rather than `Limit = 0` on purpose. In query mode `Limit` has a SECOND job:
	// it sizes the per-leg candidate depth (max(Limit, 50)), which is a SELECTION decision. Collapsing
	// "give me everything" onto Limit = 0 would silently shrink that depth to the floor and change
	// WHICH entities are candidates — a ranking change disguised as a pagination knob. So `Limit` keeps
	// meaning "the page, and the candidate depth it implies", and this flag says "don't truncate to it".
	public bool WholePool { get; init; }
}

// One read response: the selected rows plus the two cross-cutting envelopes every read
// carries — the RESPONSE BUDGET markers (Truncated/Omitted/Hint; see ResponseBudget —
// null = complete answer, so an in-budget response serializes without them) and the
// retriever PROVENANCE (which retrievers ran / degraded; null in listing mode, where no
// retriever is involved).
public sealed record SearchEnvelope<TEntity>(
	IReadOnlyList<TEntity> Items,
	bool? Truncated = null,
	int? Omitted = null,
	string? Hint = null,
	SearchRetrievers? Retrievers = null);

// The uniform service-layer read seam. Modules implement it explicitly on their existing
// service interface (e.g. ITasksService : ISearchService<...>) so every family's read has
// the same form; richer per-family overloads (extra board context, URL prefixes) may exist
// alongside — this is the common denominator, not a straitjacket.
public interface ISearchService<TEntity, TFilter, TSort>
{
	// A deliberate SHAPE constraint, not a call path: per the file header, this is "not a DI
	// seam" — modules implement it via EXPLICIT interface implementation and nothing anywhere
	// casts to ISearchService<...> to invoke it (confirmed empirically: no caller repo-wide).
	// It exists so TasksService/MemoryService's real search methods are compiler-checked against
	// one common signature, not to be dispatched through. [UsedImplicitly] rather than removing
	// the two explicit implementations and this documented conformance check.
	[UsedImplicitly]
	Task<SearchEnvelope<TEntity>> SearchAsync(string projectKey, SearchRequest<TFilter, TSort> request, CancellationToken ct = default);
}

// Axis stubs for families without a sort/filter dimension — the generic shape stays
// uniform (SearchRequest<NoFilter, NoSort>) instead of sprouting arity variants.
public readonly record struct NoSort;

public readonly record struct NoFilter;
