using LinqToDB;
using PetBox.Core.Models;

namespace PetBox.Core.Data;

// THE owner of ShareLinks: a share token is a bearer credential (Log/Share.cshtml.cs — the anonymous,
// public-facing resolve page — and PetBox.Log.Core.ShareApi, which mints one and serves its TSV, both
// touch this table and had no owner before this door).
//
// FindAsync hands back the row EXACTLY as stored (or null for "no such token") and nothing more —
// there is deliberately no ListAsync/enumeration here: a token is looked up BY VALUE only, so this
// door cannot become a way to page through every outstanding share link. It also does NOT fold in the
// expiry/scope check itself: every caller still compares `ExpiresAt` (and whatever else it checks)
// exactly as it did before this door existed — moving that comparison in here would be a change to the
// security model, which this door is explicitly not making (db-out-of-pages-remaining-24, group B).
//
// DeleteAsync (spec `share-link-revocable`) is the explicit revoke path: a HARD delete, physically
// removing the row rather than marking it — a capability-token's revoke has to leave no readable row
// at all, and a soft-delete flag is one more place the read path could forget to check. It takes
// `projectKey` as PART OF THE ADDRESS, not as a courtesy filter: mirrors
// AgentKeyAdminService.RevokeAsync's `Owned(...)` confinement — the row is matched on (Id, ProjectKey)
// together, so a caller who is honestly authorized for THEIR OWN project cannot revoke a token that
// belongs to a different one merely by knowing its value. Ownership mismatch and "no such token" are
// therefore the same outcome here (false) — the caller (ShareApi.DeleteShareAsync) answers both with
// the identical response, which is what keeps this door from becoming a cross-tenant existence oracle.
public interface IShareLinkDirectory
{
	Task<ShareLink?> FindAsync(string token, CancellationToken ct = default);

	Task CreateAsync(ShareLink link, CancellationToken ct = default);

	Task<bool> DeleteAsync(string token, string projectKey, CancellationToken ct = default);
}

public sealed class ShareLinkDirectory(ICoreDbFactory dbf) : IShareLinkDirectory
{
	public async Task<ShareLink?> FindAsync(string token, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.ShareLinks.FirstOrDefaultAsync((ShareLink s) => s.Id == token, ct);
	}

	public async Task CreateAsync(ShareLink link, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		await db.InsertAsync(link, token: ct);
	}

	public async Task<bool> DeleteAsync(string token, string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.ShareLinks
			.Where(s => s.Id == token && s.ProjectKey == projectKey)
			.DeleteAsync(token: ct) > 0;
	}
}
