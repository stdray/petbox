using PetBox.Core.Auth;
using PetBox.Core.Contract;
using PetBox.Core.Data;

namespace PetBox.Web.Mcp;

// THE MCP-SIDE HALF of work/write-body-by-reference: turning a `bodyRef` on the wire into either
// the text it names or the reason it cannot be used, and — after the write has landed — consuming
// the blob.
//
// WHY THE LOOKUP LIVES HERE AND NOT IN THE WRITE SERVICES. A blob's tenant is the project it was
// uploaded into, and whether THIS caller may read it is a question about the caller's claims —
// which exist here (ClaimsPrincipal) and nowhere below. Two concrete things break if a service
// resolves the reference against its own write target instead:
//
//   * memory_upsert / memory_remember with `scope: workspace` write into a DERIVED container
//     ($workspace / $ws-<key>) that is not a project any key is claimed on. Looking the blob up
//     there finds nothing, and every legitimate workspace write carrying a bodyRef would be refused.
//   * a wildcard (`project: *`) key has no single project of its own to look in at all.
//
// So the reference is resolved BY VALUE against the blob's own tenant, that tenant is put to
// ProjectScope — the same predicate every other surface asks — and what travels down to the service
// is the VERDICT (BodyRefResolution), never the store.
//
// SERVICES ARE PULLED FROM THE REQUEST SCOPE, not taken as parameters, and that is the pattern this
// file was told to follow rather than a shortcut: TasksTools.UpsertAsync documents in its own body
// why a widely direct-called tool must not grow a required DI parameter ("~80 existing test call
// sites" for the observation-dedup service, resolved from http.HttpContext.RequestServices for
// exactly that reason). The five verbs `bodyRef` lands on are the most direct-called in the suite.
// When there is no request scope at all (a hand-built accessor in a unit test), a bodyRef is
// REFUSED rather than ignored — see UnavailableBatch.
//
// TWO PHASES, and that split is the whole of the one-shot semantics being safe:
//
//   RESOLVE reads. It does not delete. A write can still be refused downstream on a stale version
//   watermark, and a blob eaten by a rejected write would force a re-upload before every CAS retry
//   — which is precisely the "pay for the body twice" this mechanism abolishes, reintroduced at the
//   retry.
//
//   CONSUME deletes, and only for the items that actually LANDED. That is the honest reading of
//   "consumed at substitution": the substitution happened, so the blob is spent. Nothing leaks
//   either — an unconsumed blob expires on its TTL and BodyRefPruneJob reclaims it.
static class McpBodyRefs
{
	// Resolve every DISTINCT reference of one call, once. A tasks_upsert naming the same blob from
	// two nodes reads it once and — importantly — BOTH nodes get the text, rather than the first
	// consuming it and the second failing.
	public static async Task<BodyRefBatch> ResolveAsync(
		IHttpContextAccessor http, IEnumerable<string?> references, CancellationToken ct)
	{
		var wanted = Distinct(references);
		if (wanted.Count == 0) return BodyRefBatch.Empty;

		var services = http.HttpContext?.RequestServices;
		var blobs = services?.GetService<IBodyRefBlobStore>();
		var catalog = services?.GetService<IProjectCatalog>();
		// No request scope, or the store is not registered: the reference cannot be judged, so it is
		// REFUSED. Passing null through as "no bodyRef" would silently drop the caller's body and
		// write an empty one — a lost mutation, the very class the unknown-parameter filter exists
		// to prevent elsewhere.
		if (blobs is null || catalog is null)
			return BodyRefBatch.AllUnresolvable(wanted);

		var user = http.HttpContext?.User;
		var now = DateTime.UtcNow;
		var resolutions = new Dictionary<string, BodyRefResolution>(StringComparer.Ordinal);
		var tenants = new Dictionary<string, string>(StringComparer.Ordinal);

		foreach (var reference in wanted)
		{
			// Shape first, and it earns its own distinct message: "blob-<32 hex>" is what the upload
			// endpoint hands back, and a caller who pasted a file PATH or a whole URL there needs to
			// be told which of the two things they got wrong. Every OTHER failure below is
			// deliberately the SAME message.
			if (!BodyRefs.IsWellFormed(reference))
			{
				resolutions[reference] = BodyRefResolution.Failed(reference, BodyRefs.Malformed(reference));
				continue;
			}

			var blob = await blobs.PeekAsync(reference, now, ct);
			// Absent, expired, already consumed, or belonging to a project this caller may not read.
			// All four collapse into ONE message (BodyRefs.Unresolvable): telling them apart would
			// let a caller probe for the existence of another tenant's blob, and the remedy is
			// identical in every case — upload the file again.
			if (blob is null ||
				await ProjectScope.EvaluateAsync(user, blob.ProjectKey, catalog, ct) != ProjectAccess.Allowed)
			{
				resolutions[reference] = BodyRefResolution.Failed(reference, BodyRefs.Unresolvable(reference));
				continue;
			}

			resolutions[reference] = BodyRefResolution.Resolved(reference, blob.Body);
			tenants[reference] = blob.ProjectKey;
		}

		return new BodyRefBatch(blobs, resolutions, tenants);
	}

	internal static List<string> Distinct(IEnumerable<string?> references) =>
		[.. references.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).Distinct(StringComparer.Ordinal)];
}

// The resolved references of ONE call, plus the door that spends them.
sealed class BodyRefBatch
{
	readonly IBodyRefBlobStore? _blobs;
	readonly IReadOnlyDictionary<string, BodyRefResolution> _resolutions;
	readonly IReadOnlyDictionary<string, string> _tenants;

	internal BodyRefBatch(
		IBodyRefBlobStore? blobs,
		IReadOnlyDictionary<string, BodyRefResolution> resolutions,
		IReadOnlyDictionary<string, string> tenants)
	{
		_blobs = blobs;
		_resolutions = resolutions;
		_tenants = tenants;
	}

	public static BodyRefBatch Empty { get; } = new(
		null,
		new Dictionary<string, BodyRefResolution>(StringComparer.Ordinal),
		new Dictionary<string, string>(StringComparer.Ordinal));

	// Every reference refused with the ordinary "not available" message, and no tenants — so
	// nothing is consumable either. Used when the store cannot be reached at all.
	internal static BodyRefBatch AllUnresolvable(IEnumerable<string> references) => new(
		null,
		references.ToDictionary(r => r, r => BodyRefResolution.Failed(r, BodyRefs.Unresolvable(r)), StringComparer.Ordinal),
		new Dictionary<string, string>(StringComparer.Ordinal));

	public bool IsEmpty => _resolutions.Count == 0;

	// The verdict for one item's `bodyRef`, or null when the item sent none. Every reference that
	// was in the call is in the map, so null here means "no bodyRef" and never "not looked up".
	public BodyRefResolution? For(string? reference) =>
		string.IsNullOrWhiteSpace(reference) ? null : _resolutions.GetValueOrDefault(reference);

	// Spend the blobs behind `references` — called ONLY for items the write actually applied.
	// Best-effort by design: the write has already landed and is not going to be undone because a
	// DELETE lost a race with a concurrent consumer of the same blob. A blob that survives this call
	// is still bounded by its TTL.
	public async Task ConsumeAsync(IEnumerable<string?> references, CancellationToken ct)
	{
		if (_blobs is null) return;
		foreach (var reference in McpBodyRefs.Distinct(references))
			// Only a reference this call RESOLVED is consumable: the tenant map is populated exactly
			// where authorization passed, so a malformed or unauthorized reference cannot reach a
			// DELETE even if its key somehow appeared in the applied set.
			if (_tenants.TryGetValue(reference, out var project))
				await _blobs.ConsumeAsync(reference, project, ct);
	}
}
