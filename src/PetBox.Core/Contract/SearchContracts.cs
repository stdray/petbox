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
public sealed record SearchRequest<TFilter, TSort>
{
	public string? Query { get; init; }
	public TFilter? Filter { get; init; }
	public (TSort By, bool Desc)? Sort { get; init; }
	public int Limit { get; init; }
	public int BodyLen { get; init; }
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
	Task<SearchEnvelope<TEntity>> SearchAsync(string projectKey, SearchRequest<TFilter, TSort> request, CancellationToken ct = default);
}

// Axis stubs for families without a sort/filter dimension — the generic shape stays
// uniform (SearchRequest<NoFilter, NoSort>) instead of sprouting arity variants.
public readonly record struct NoSort;

public readonly record struct NoFilter;
