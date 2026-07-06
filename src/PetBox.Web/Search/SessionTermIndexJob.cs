using PetBox.Sessions.Search;

namespace PetBox.Web.Search;

// Thin IVectorizationJob adapter over ISessionTermIndex.DrainAllAsync (spec:
// session-discovery-verbatim) — rides the SAME background enrichment tick as
// SessionDigestJob/SessionFactsJob (SearchVectorizationService), but the term index itself
// lives in PetBox.Sessions (it needs no LLM and no memory store, unlike the digest). This
// class exists only so the maintenance pass is discoverable through the shared
// IVectorizationJob registration list Program.cs already loops every tick.
public sealed class SessionTermIndexJob(ISessionTermIndex termIndex) : IVectorizationJob
{
	public Task<int> DrainAllAsync(CancellationToken ct) => termIndex.DrainAllAsync(ct);
}
