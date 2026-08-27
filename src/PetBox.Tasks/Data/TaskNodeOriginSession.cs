using LinqToDB.Mapping;

namespace PetBox.Tasks.Data;

// The ACCUMULATING half of node provenance (node-origin-provenance): every session that has
// touched this node, bound to its stable NodeId (so it survives renames). Deliberately NOT
// SCD-2 like TaskNodeCommit: a commit set is REPLACED by a write (null=omit / []=clear / a
// list replaces), so it needs open/close history to answer "what did it carry then". This set
// is only ever ADDED to — a session that touched the node once touched it forever — so there
// is nothing to close and no ValidTo to carry.
//
// UNION, NOT A LOG — this is the explicit answer to "one session touches the node twice":
// (NodeId, SessionId) is the PRIMARY KEY, so the second touch cannot become a second row. The
// fold is done at the DB level rather than in the writer so that two concurrent writers racing
// on the same (node, session) still cannot produce a duplicate. `FirstSeen` is therefore
// literally first-seen: it is never overwritten by a later touch.
//
// This association is NOT part of TaskNode.SamePayload/ChangedPayloadFields — the whole reason
// it is an association and not a field (owner decision 2026-08-27, by the `Commits` precedent
// at TaskNode.cs "never mints a node revision"): a bucket of N sessions carried as a node field
// would mint N node revisions, one per recurrence.
[Table("plan_node_sessions")]
public sealed record TaskNodeOriginSession
{
	[Column, NotNull] public string NodeId { get; init; } = string.Empty;
	// Denormalized mirror of the node's partition, so a board-scoped read needs no join —
	// same reason TaskNodeCommit carries it.
	[Column, NotNull] public string Board { get; init; } = string.Empty;
	[Column, NotNull] public string SessionId { get; init; } = string.Empty;
	[Column, NotNull] public DateTime FirstSeen { get; init; }
}
