using LinqToDB;
using PetBox.Core.Contract;
using PetBox.Core.Models;

namespace PetBox.Core.Data;

// THE owner of BodyRefBlobs (work/write-body-by-reference). Modelled on IShareLinkDirectory, and
// for the same reasons — a blob reference is a bearer-ish handle, so this door deliberately offers
// no enumeration at all: a blob is looked up BY VALUE, and only together with the tenant it belongs
// to. There is no ListAsync, so this cannot become a way to page through another project's pending
// uploads, and there is no DeleteAsync(ref) either — the only deletions are CONSUMPTION (by the
// write that referenced it) and EXPIRY (the prune job).
//
// TENANT IS PART OF THE ADDRESS. TakeAsync matches on (Ref, ProjectKey) together — the same
// confinement ShareLinkDirectory.DeleteAsync applies — so a caller honestly authorized for their own
// project cannot reach a blob of another one merely by holding its reference. "Wrong tenant" and "no
// such blob" are therefore the SAME outcome (null), which is what keeps this from being a
// cross-tenant existence oracle. The caller (McpBodyRefs) renders both with one message.
//
// EXPIRY IS ENFORCED ON READ, not only by the prune job. The job is a background sweep and can be
// arbitrarily late (a paused host, a disabled tick); a blob past ExpiresAt must be unusable the
// instant it is past it, not the next time a job happens to run. The job reclaims space; the read
// predicate is what enforces the TTL.
public interface IBodyRefBlobStore
{
	Task PutAsync(BodyRefBlob blob, CancellationToken ct = default);

	// Look up a live blob BY VALUE — without consuming it. Null when there is no such blob or it is
	// past ExpiresAt.
	//
	// NO TENANT ARGUMENT, and that is deliberate rather than an omission. The caller (McpBodyRefs)
	// cannot supply one: a wildcard key's project claim is `*`, and an MCP call's `projectKey`
	// argument names the WRITE TARGET, which for a workspace-scoped memory write is a container the
	// blob provably does not live in. So the row is fetched by its unguessable 128-bit reference and
	// the tenant it names is then put to ProjectScope — the caller authorizes against the blob's OWN
	// project instead of guessing at it. Nothing about the row reaches the caller of the TOOL before
	// that check; this is an in-process read, and "wrong tenant" and "no such blob" collapse into one
	// answer at the surface (BodyRefs.Unresolvable), so this is not an existence oracle. It is the
	// same by-value-only posture IShareLinkDirectory.FindAsync takes, for the same reason.
	Task<BodyRefBlob?> PeekAsync(string reference, DateTime now, CancellationToken ct = default);

	// The one-shot half: delete the row. Returns true when this call is the one that removed it, so
	// two concurrent writes racing on the same reference cannot both report a consumption.
	Task<bool> ConsumeAsync(string reference, string projectKey, CancellationToken ct = default);

	// Background reclamation of blobs nobody consumed. Returns how many rows went.
	Task<int> PruneExpiredAsync(DateTime now, CancellationToken ct = default);
}

public sealed class BodyRefBlobStore(ICoreDbFactory dbf) : IBodyRefBlobStore
{
	public async Task PutAsync(BodyRefBlob blob, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		await db.InsertAsync(blob, token: ct);
	}

	public async Task<BodyRefBlob?> PeekAsync(string reference, DateTime now, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.BodyRefBlobs
			.Where(b => b.Ref == reference && b.ExpiresAt > now)
			.FirstOrDefaultAsync(ct);
	}

	public async Task<bool> ConsumeAsync(string reference, string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.BodyRefBlobs
			.Where(b => b.Ref == reference && b.ProjectKey == projectKey)
			.DeleteAsync(token: ct) > 0;
	}

	public async Task<int> PruneExpiredAsync(DateTime now, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.BodyRefBlobs.Where(b => b.ExpiresAt <= now).DeleteAsync(token: ct);
	}

	// The row an upload turns into. Here rather than in the endpoint so the TTL is applied in ONE
	// place and an endpoint cannot mint a blob that outlives BodyRefs.Ttl.
	public static BodyRefBlob NewBlob(string projectKey, string body, long bytes, string createdBy, DateTime now) =>
		new()
		{
			Ref = BodyRefs.NewReference(),
			ProjectKey = projectKey,
			Body = body,
			Bytes = bytes,
			CreatedAt = now,
			ExpiresAt = now + BodyRefs.Ttl,
			CreatedBy = createdBy,
		};
}
