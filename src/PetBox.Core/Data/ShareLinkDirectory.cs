using LinqToDB;
using LinqToDB.Async;
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
public interface IShareLinkDirectory
{
	Task<ShareLink?> FindAsync(string token, CancellationToken ct = default);

	Task CreateAsync(ShareLink link, CancellationToken ct = default);
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
}
