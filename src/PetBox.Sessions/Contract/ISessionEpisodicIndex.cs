using PetBox.Core.Search;

namespace PetBox.Sessions.Contract;

// Full-text + semantic retrieval WITHIN one session, hydrated on demand (spec:
// session-episodic-lazy). There is no always-on index over the session archive — a
// session's transient index is built when first searched and aged out by idleness, so
// the archive can grow without a resident global index. Implementations are pluggable
// by location behind this contract (spec: search-pluggable-location).
public interface ISessionEpisodicIndex
{
	// Hydrates (or reuses) the session's transient index and searches inside it.
	// Null when the session does not exist (or is deleted).
	Task<SessionEpisodicResult?> SearchAsync(string projectKey, string sessionId, string query, int k, CancellationToken ct = default);

	// Drops hydrated sessions idle past the TTL (and trims over capacity); returns how
	// many were evicted. Runs implicitly on every search — exposed for tests/ops.
	int EvictIdle();
}

// One match inside a session: the message ordinal (the provenance bridge — feed it to
// session.get to reach the verbatim source), its role, a display snippet, the per-index
// score and which retriever surfaced it.
public sealed record SessionEpisodicHit(long Message, string Role, string Snippet, double Score, string? Retriever);

public sealed record SessionEpisodicResult(IReadOnlyList<SessionEpisodicHit> Hits, SearchRetrievers Retrievers);
