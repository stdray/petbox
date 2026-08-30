using LinqToDB.Mapping;

namespace PetBox.Core.Models;

// A public link onto ONE task node — spec `node-share`. Its own table, deliberately NOT a widened
// ShareLink: that row's LogName/Kql/ColumnsJson/ModesJson are all NOT NULL and describe a log
// export, so generalizing it would smear nullable columns across a working feature to make room for
// a second, differently-shaped grant. Two tables, one revoke path (IShareRevocationService).
//
// Like ShareLink this is a capability token: `Id` IS the grant, and the ROW — not the caller —
// names the project, the board, the node, the comment and how much of it may be read. Nothing else
// is presented at read time, which is why the public reader can be anonymous.
[Table("node_shares")]
public sealed record NodeShare
{
	// The token itself: 20 random bytes, hex-lowercase (40 chars) — the same shape and the same
	// entropy as ShareLink.Id, minted the same way.
	[Column, PrimaryKey, NotNull]
	public string Id { get; init; } = string.Empty;

	[Column, NotNull]
	public string ProjectKey { get; init; } = string.Empty;

	// The board the node lives on. Part of the ADDRESS, not decoration: the future public reader
	// resolves (ProjectKey, Board, NodeId) without asking the caller for any of them.
	[Column, NotNull]
	public string Board { get; init; } = string.Empty;

	// The stable 32-hex TaskNode.NodeId — never a slug. A slug can be renamed; a link that pointed
	// at one would then resolve to a different node or to nothing.
	[Column, NotNull]
	public string NodeId { get; init; } = string.Empty;

	// Only for Scope == comment; null for body/full. Enforced at the mint surface, which also
	// proves the comment really hangs under NodeId — otherwise a link could publish one node's
	// comment under another node's identity.
	[Column, Nullable]
	public string? CommentId { get; init; }

	// How much of the node the token publishes: NodeShareScopes.Body | Comment | Full.
	[Column, NotNull]
	public string Scope { get; init; } = NodeShareScopes.Body;

	[Column]
	public DateTime CreatedAt { get; init; }

	[Column, NotNull]
	public string CreatedBy { get; init; } = "system";

	// NULLABLE, and that is the feature (spec `node-share-lifetime`): null means the link never
	// expires — it is not "expired", it has no expiry at all, and retention's sweep does not pick it
	// up. A link is then withdrawn by REVOKING it, never by waiting.
	[Column, Nullable]
	public DateTime? ExpiresAt { get; init; }

	// The ONE definition of "this link has run out", so the reader, the retention sweep and the
	// tests cannot each invent their own null handling. A method, not a property: a get-only
	// property would still be walked by FluentMappingCompletenessTests' model sweep, and this is
	// not a column.
	public bool IsExpiredAt(DateTime utcNow) => ExpiresAt is { } expires && expires < utcNow;
}

// The closed vocabulary of `NodeShare.Scope` (spec `node-share-scope`) — the extent of the grant is
// chosen when the link is MINTED, so it is stored, not negotiated at read time.
public static class NodeShareScopes
{
	// The node's own body and title; no comment thread.
	public const string Body = "body";

	// Exactly ONE comment (NodeShare.CommentId) under the node.
	public const string Comment = "comment";

	// The node plus its whole comment thread.
	public const string Full = "full";

	public static bool IsValid(string? scope) =>
		scope is Body or Comment or Full;
}
