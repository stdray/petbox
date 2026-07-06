namespace PetBox.Sessions.Search;

// The verbatim discovery leg for session search (spec: session-discovery-verbatim): a
// per-session FULL-TEXT index over the raw transcript content — not the LLM digest, which
// can (and does) drop distinctive terms the model judged non-essential to the summary. A
// session found ONLY through this leg still surfaces at stage-1 discovery, fused with the
// digest leg by the same RRF primitive the rest of the system uses (spec: search-fair-fusion).
public interface ISessionTermIndex
{
	// BM25-ranked session ids matching `query` (best first), capped at `k`. Empty when the
	// query has no searchable tokens or nothing matches. `k` is a POOL size for the caller's
	// own fusion/re-ranking, not a final result count.
	Task<IReadOnlyList<string>> SearchAsync(string projectKey, string query, int k, CancellationToken ct = default);

	// Maintenance pass over every project: (re)index any session whose header Version moved
	// past its term-index cursor (a missing cursor row defaults to 0, so a pre-existing
	// session backfills on the first pass). No LLM/chat capability is involved — this is a
	// pure tokenization pass, independent of digest distillation. Returns the count of
	// sessions (re)indexed this pass, for the shared vectorization-tick logging.
	Task<int> DrainAllAsync(CancellationToken ct = default);
}
