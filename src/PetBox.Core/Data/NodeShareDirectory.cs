using LinqToDB;
using PetBox.Core.Models;

namespace PetBox.Core.Data;

// THE owner of `node_shares`, modelled line for line on IShareLinkDirectory (read that file first —
// the reasoning is the same and is not repeated here). The three points worth restating because a
// second table is where a security model quietly diverges:
//
//   * NO enumeration. FindAsync only, by token VALUE. There is no ListAsync, so this door cannot
//     become a way to page through every outstanding link on a project.
//   * FindAsync does NOT fold in expiry. It hands back the row exactly as stored; the caller
//     compares NodeShare.IsExpiredAt itself, exactly as ShareApi.GetTsvAsync does for a log link.
//     That matters more here than there: a NULL ExpiresAt means "never expires" (spec
//     `node-share-lifetime`), and hiding that decision inside the lookup would put the one novel
//     rule of this feature somewhere no caller can see it.
//   * DeleteAsync takes `projectKey` as PART OF THE ADDRESS. The row is matched on (Id, ProjectKey)
//     together, so a caller honestly authorized for THEIR OWN project cannot revoke someone else's
//     token merely by knowing its value — and "not yours" and "no such token" collapse to the same
//     `false`, which is what keeps the revoke surface from becoming a cross-tenant existence oracle.
public interface INodeShareDirectory
{
	Task<NodeShare?> FindAsync(string token, CancellationToken ct = default);

	Task CreateAsync(NodeShare share, CancellationToken ct = default);

	Task<bool> DeleteAsync(string token, string projectKey, CancellationToken ct = default);
}

public sealed class NodeShareDirectory(ICoreDbFactory dbf) : INodeShareDirectory
{
	public async Task<NodeShare?> FindAsync(string token, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.NodeShares.FirstOrDefaultAsync((NodeShare s) => s.Id == token, ct);
	}

	public async Task CreateAsync(NodeShare share, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		await db.InsertAsync(share, token: ct);
	}

	public async Task<bool> DeleteAsync(string token, string projectKey, CancellationToken ct = default)
	{
		using var db = dbf.Open();
		return await db.NodeShares
			.Where(s => s.Id == token && s.ProjectKey == projectKey)
			.DeleteAsync(token: ct) > 0;
	}
}
