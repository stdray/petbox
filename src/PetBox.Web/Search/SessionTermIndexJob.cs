using PetBox.Sessions.Search;

namespace PetBox.Web.Search;

// Thin IBackgroundIndexJob adapter over ISessionTermIndex.DrainAllAsync (spec:
// session-discovery-verbatim) — rides the SAME background enrichment tick as
// SessionDigestJob/SessionFactsJob (SearchEnrichmentService), but the term index itself
// lives in PetBox.Sessions (it needs no LLM and no memory store, unlike the digest). This
// class exists only so the maintenance pass is discoverable through the shared
// IBackgroundIndexJob registration list Program.cs already loops every tick. It only
// tokenizes (a lexical FTS index, no vectors) — a concrete reason the interface is named for
// background indexing generally, not vectorization.
public sealed class SessionTermIndexJob(ISessionTermIndex termIndex) : IBackgroundIndexJob
{
	public Task<int> DrainAllAsync(CancellationToken ct) => termIndex.DrainAllAsync(ct);
}
