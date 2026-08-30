using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PetBox.Core.Auth;
using PetBox.Core.Data;
using PetBox.Core.Models;
using PetBox.Core.Settings;
using PetBox.Tasks.Contract;
using PetBox.Web.Pages.Shared;
using PetBox.Web.Rendering;

namespace PetBox.Web.Pages;

[AllowAnonymous]
// The node twin of ShareModel, and the declaration is the same one for the same reason (read that
// file first — the argument is not repeated here). /ui/share/node/{token} has NO tenant in its
// route: `{token}` is looked up (INodeShareDirectory) and the STORED ROW — not the caller — names
// the project, the board, the node, the comment and the SCOPE of exactly how much may be read. The
// caller supplies a token and nothing else; there is no field on this surface through which a
// tenant could be aimed.
//
// The one thing this page must get right that the log twin did not have to: expiry is NULLABLE
// here. `ExpiresAt = null` means the link never expires (spec `node-share-lifetime`), so the check
// below goes through NodeShare.IsExpiredAt — the ONE null-safe predicate — rather than a
// hand-written `< DateTime.UtcNow`, which on a null would silently compare against default(DateTime)
// and 404 every permanent link ever minted.
[TenantExempt(TenantExemption.CapabilityToken,
	"the share token IS the grant: issued by an explicit act, and the stored row — not the caller — "
	+ "names the project, board, node, comment and the scope of exactly what may be read")]
public sealed class ShareNodeModel : PageModel
{
	readonly INodeShareDirectory _shares;
	readonly ITasksService _tasks;
	readonly ICommentService _comments;
	readonly ISettingsResolver _settings;

	public ShareNodeModel(
		INodeShareDirectory shares, ITasksService tasks, ICommentService comments, ISettingsResolver settings)
	{
		_shares = shares;
		_tasks = tasks;
		_comments = comments;
		_settings = settings;
	}

	[BindProperty(SupportsGet = true)]
	public string Token { get; set; } = string.Empty;

	// The PROJECTION, not the NodeDetailView. This page deliberately does not hold a
	// NodeDetailView at all: that record carries Ancestors and Relations, and a view model that
	// HAS them is one Razor edit away from rendering them. What cannot be reached cannot leak — so
	// the cut happens here, on the way in, and the fields the card lists as "cut"
	// (OriginSessionId, OriginSessions, DecisionPending, Observation, Relations, Ancestors) have no
	// property on this page to be read from.
	public sealed record PublicNode(
		string Key, string NodeId, string Title, string Status, string Type, string Body,
		IReadOnlyList<string> Tags, long Priority, IReadOnlyList<string> Commits,
		DateTime? Created, DateTime? Updated);

	public PublicNode? Node { get; private set; }

	// Scope, verbatim from the stored row — the extent was chosen when the link was MINTED and is
	// not negotiated at read time. The view branches on this, never on a query parameter.
	public string Scope { get; private set; } = NodeShareScopes.Body;

	// Already DFS-flattened for _CommentThread. Empty for scope=body; exactly ONE line for
	// scope=comment; the whole thread for scope=full.
	public IReadOnlyList<CommentLine> Thread { get; private set; } = [];

	public string? CommitUrlTemplate { get; private set; }

	// `[[#comment]]` references (comment-slug-and-refs), built from `Thread` — the comments this
	// page is ACTUALLY RENDERING — and from nothing else. That one sentence is the whole confinement
	// story for this feature, and it is the same shape as the NodeRefs/MemoryRefs decision above
	// (see the Razor's header): the page withholds DATA, the renderer grows no "public" mode.
	//
	// It follows, with no branch anywhere, that:
	//   scope=body    → Thread is empty  → an EMPTY map → every reference in the body is plain text;
	//   scope=comment → Thread is the ONE published comment → a self-reference links, a reference to
	//                   a neighbour is plain text — so the link neither leads into a UI this reader
	//                   cannot open nor discloses that the neighbour exists;
	//   scope=full    → the whole published thread → references work inside the share.
	public IReadOnlyDictionary<string, NodeRefTarget> CommentRefs { get; private set; }
		= new Dictionary<string, NodeRefTarget>(StringComparer.Ordinal);

	public bool ShowBody => Scope != NodeShareScopes.Comment;

	public async Task<IActionResult> OnGetAsync(CancellationToken ct)
	{
		var share = await _shares.FindAsync(Token, ct);

		// ONE refusal for four different situations — no such token, revoked, expired, and a row
		// whose node has since been deleted. A page that distinguished them would turn an anonymous
		// surface into an oracle: "expired" tells a stranger the token was once real, and
		// "node gone" tells them the project still exists. NotFound() also means a revoke (a hard
		// delete, see IShareRevocationService) stops the page serving on the very next request, with
		// no cache of its own to go stale.
		if (share is null || share.IsExpiredAt(DateTime.UtcNow))
			return NotFound();

		var detail = await _tasks.GetNodeAsync(share.ProjectKey, share.NodeId, ct);
		if (detail is null)
			return NotFound();

		// The board is part of the token's ADDRESS. A row whose board no longer matches where the
		// node lives is not the grant that was issued, so it is refused rather than served from
		// wherever the node moved to.
		if (!string.Equals(detail.Board, share.Board, StringComparison.Ordinal))
			return NotFound();

		var n = detail.Node;
		Scope = share.Scope;
		Node = new PublicNode(
			n.Key, n.NodeId, n.Title, n.Status, n.Type, n.Body,
			n.Tags, n.Priority, n.Commits, n.CreatedAt, n.UpdatedAt);

		// An external VCS URL, not a leak: it links a commit hash out to the repo browser, which is
		// the same place the sha in the body already points a reader who types it in.
		// Fully qualified: this class's own `Scope` property (the share's extent) shadows the
		// settings-cascade `Scope` enum, and the two mean entirely different things.
		CommitUrlTemplate = (await _settings.GetAsync<RepoSettings>(
			PetBox.Core.Settings.Scope.Project, share.ProjectKey, ct)).CommitUrlTemplate;

		Thread = await BuildThreadAsync(share, detail.Board, ct);
		// Built from Thread, not from the node's comments — see the property's own note. The
		// distinction is the feature: BuildThreadAsync has already applied the token's scope, so this
		// map cannot hold a comment the reader is not being shown, whatever the body mentions.
		CommentRefs = CommentRefMap.Build(Thread.Select(l => l.Comment));
		return Page();
	}

	// The three scopes, in one place, so "what does this token publish" is answered once rather
	// than by three conditions spread through the Razor.
	async Task<IReadOnlyList<CommentLine>> BuildThreadAsync(
		NodeShare share, string board, CancellationToken ct)
	{
		if (share.Scope == NodeShareScopes.Body)
			return [];

		var comments = await _comments.ListForNodeAsync(share.ProjectKey, board, share.NodeId, ct);

		if (share.Scope == NodeShareScopes.Full)
			return CommentThread.Flatten(comments);

		// scope=comment publishes EXACTLY the one comment named on the row — not its replies and
		// not its neighbours. Depth is forced to 0: the comment's real depth in the thread is a
		// fact about the comments around it, and indenting it would both look wrong standing alone
		// and quietly disclose that there are siblings above it.
		var one = comments.FirstOrDefault(c => string.Equals(c.Id, share.CommentId, StringComparison.Ordinal));
		return one is null ? [] : [new CommentLine(one, 0)];
	}
}
