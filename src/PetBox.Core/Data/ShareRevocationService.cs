namespace PetBox.Core.Data;

// ONE revoke, TWO token families (spec `share-link-revocable`, work `node-share-backend`).
//
// A share token is opaque by design — nothing in `a1b2c3…` says whether it names a log export
// (ShareLinks) or a published task node (node_shares). The person revoking one holds the URL, not
// the table name, so "which directory is this in" is the SYSTEM's question, not the caller's: node
// sharing therefore gets NO revoke endpoint and NO revoke tool of its own. DELETE
// /api/share/{token} and mcp:share_revoke keep being the whole revoke surface and grow a second
// place to look.
//
// This service exists so that rule is written ONCE. The two transports are otherwise identical
// (spec `access-permission-uniform` — one domain action must not answer differently depending on
// how it arrived), and a copy of `logs first, then nodes` in each of them is exactly how they would
// drift: a third table, or a change of order, would have to be remembered twice.
//
// ORDER — logs first. Not arbitrary: it keeps the log path byte-for-byte what it was (one query,
// same answer) so nothing about the shipped feature changes, and only a token that is NOT a log
// link pays for the second lookup.
//
// The tenant confinement is NOT here. It stays in each directory's DeleteAsync, which matches
// (Id, ProjectKey) TOGETHER — so this method inherits, from both, the property that a foreign
// token is simply not found. `false` therefore means "no such token under this project", covering
// never-existed, already-revoked and belongs-to-someone-else with one indistinguishable answer;
// both callers turn it into their own 404-shaped refusal.
public interface IShareRevocationService
{
	Task<bool> RevokeAsync(string token, string projectKey, CancellationToken ct = default);
}

public sealed class ShareRevocationService(
	IShareLinkDirectory shareLinks,
	INodeShareDirectory nodeShares) : IShareRevocationService
{
	public async Task<bool> RevokeAsync(string token, string projectKey, CancellationToken ct = default) =>
		await shareLinks.DeleteAsync(token, projectKey, ct)
		|| await nodeShares.DeleteAsync(token, projectKey, ct);
}
