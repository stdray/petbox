using System.ComponentModel;
using ModelContextProtocol.Server;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Web.Mcp.Contract;

namespace PetBox.Web.Mcp;

// The MCP half of spec `share-link-revocable`. The REST half (DELETE /api/share/{token},
// PetBox.Log.Core.ShareApi.DeleteShareAsync) closed the hole — a bearer credential that could not be
// withdrawn before its TTL — and this verb is what makes the withdrawal reachable to an AGENT.
// Without it the only way to revoke was a hand-written curl: the surface existed, the caller who
// needs it most could not address it.
//
// WHY THERE IS NO share_create HERE. Minting is a UI act (the Logs page's Share button posts
// /api/share and shows the link); an agent has log_query and needs no token to read what it may
// already read. Revoking is the opposite — it is the act somebody performs ABOUT a token that is
// already loose, often in a hurry, and often by an operator agent rather than by the browser that
// minted it. One direction of the pair is on the MCP surface because only one direction of the pair
// has a caller here.
//
// SCOPE: NONE BEYOND AN AUTHENTICATED, TENANT-AUTHORIZED KEY — deliberately, and this is the one
// judgement call in the file. The spec's criterion for revoke is "an explicit action by someone
// ENTITLED TO MINT THE SAME TOKEN", and minting (ShareApi.CreateShareAsync) requires exactly
// authentication plus authorization for the project in the body — no ApiKeyScope at all. Gating this
// verb on, say, logs:query would make REVOKING a link strictly harder than ISSUING one, so a key that
// could hand out an export link could not take it back; that is the wrong direction for a safety
// action, and it would also split one domain action into two different answers depending on the
// transport it arrived through (spec `access-permission-uniform`). whoami is the existing precedent
// for a scope-free tool; the difference is that this one WRITES, which is precisely why the tenant
// axis below is not optional.
//
// TENANT DECLARATION (spec authz-scope-declaration): the `projectKey` ARGUMENT — the MCP spelling of
// the REST twin's [TenantFrom(BodyField, "projectKey")], enforced by McpTenantEnforcementFilter
// before the body runs. It proves the caller owns SOME project they honestly named; it does NOT
// prove they own THIS share's project. That second half is IShareLinkDirectory.DeleteAsync's, which
// matches the row on (token, projectKey) TOGETHER — so a foreign token is simply not found, and
// "no such token" and "not yours" collapse into one answer on purpose (a distinguishable pair would
// be a cross-tenant existence oracle over share tokens).
[McpServerToolType]
[TenantFrom(TenantSource.Argument, "projectKey")]
public static class ShareTools
{
	[McpServerTool(Name = "share_revoke", Title = "Revoke a share link", Destructive = true, UseStructuredContent = true, OutputSchemaType = typeof(ShareRevokedResult))]
	[Description("Revokes a share link (the token minted by the Logs page's Share button / POST /api/share), immediately and independently of its TTL: the row is HARD-deleted, so both the anonymous TSV export (GET /api/share/{token}/tsv) and the anonymous HTML page (/ui/share/{token}) stop serving it on the very next request. Takes `projectKey` (the project that OWNS the link — must match the calling ApiKey's project claim) and `token` (the opaque id in the share URL). A token that does not exist under that project — never existed, already revoked, or belongs to a DIFFERENT project — answers with the identical 'share link not found' error, so this verb cannot be used to probe which tokens exist elsewhere. There is deliberately no companion 'list share links' verb: a token is addressable by value only. Requires no scope beyond an authenticated key authorized for `projectKey` — the same entitlement that mints the link.")]
	public static async Task<ShareRevokedResult> RevokeAsync(
		IShareLinkDirectory shareLinks,
		[Description("Project key that owns the share link — must match the calling ApiKey's project claim.")] string projectKey,
		[Description("The opaque share token: the last path segment of /ui/share/{token}.")] string token,
		CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required");

		var revoked = await shareLinks.DeleteAsync(token, projectKey, ct);

		// Same wording, and the same indistinguishability, as the REST twin's 404 body. An
		// InvalidOperationException (not Unauthorized) because it is NOT an authorization answer: the
		// PEP already allowed this caller into this project, and what happened here is that the
		// addressed row is not in it.
		if (!revoked) throw new InvalidOperationException("share link not found");

		return new ShareRevokedResult(true, token);
	}
}
