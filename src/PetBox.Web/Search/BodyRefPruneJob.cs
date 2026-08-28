using PetBox.Core.Data;

namespace PetBox.Web.Search;

// Reclamation of blobs nobody consumed (work/write-body-by-reference).
//
// A blob is ONE-SHOT: the write that references it deletes it. Everything else that can happen to
// an upload leaves a row behind — the caller uploaded and then decided not to write, the write was
// refused on a stale watermark and never retried, the agent's session ended between the two calls.
// Without this job every one of those rows is permanent, and a table advertised as a TRANSPORT
// quietly becomes a store, which is exactly the outcome the card's shape decision rejected.
//
// It rides the SAME background enrichment tick as the vector/digest jobs (SearchEnrichmentService
// loops every registered IBackgroundIndexJob), which is why it is shaped as one despite indexing
// nothing — the same reason SessionTermIndexJob is registered here rather than inventing a second
// scheduler. The interface is named for background WORK, not specifically for vectorization; its
// own header says so.
//
// EXPIRY IS NOT ENFORCED HERE. BodyRefBlobStore.PeekAsync already excludes anything past ExpiresAt,
// so a blob is unusable the instant its TTL passes whether or not this job has run. That ordering
// matters: a background sweep can be arbitrarily late (a paused host, a disabled tick, a crash
// loop), and a TTL that only held when a job happened to run would be a security property depending
// on a scheduler. This job reclaims SPACE; the read predicate enforces the DEADLINE.
public sealed class BodyRefPruneJob(IBodyRefBlobStore blobs) : IBackgroundIndexJob
{
	public Task<int> DrainAllAsync(CancellationToken ct) => blobs.PruneExpiredAsync(DateTime.UtcNow, ct);
}
