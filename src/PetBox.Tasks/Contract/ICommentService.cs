namespace PetBox.Tasks.Contract;

// The single door to node comments — a generic, editable, tree-structured comment thread
// under any plan node, on any board. Deliberately SEPARATE from ITasksService: comments
// are not PlanNodes, so keeping them off the tasks door guarantees they never leak into
// tasks_search / the workflow FSM / delivery roll-ups. The Web layer (MCP tools + the board
// page) depends only on this interface (TasksBoundaryTests forbids touching the store/db).
public interface ICommentService
{
	// Batch declarative upsert of comments on a board (uniform-entity-verbs, mirrors
	// tasks_upsert). Each item with a null/empty Id is a CREATE (needs a RESOLVED NodeId +
	// author; ParentId optional = reply); an item with an Id is a PATCH of body/tags under a
	// `version` WATERMARK. NodeId is the already-resolved 32-hex owner (the adapter resolves a
	// slug on `board`).
	// `atomic` (default TRUE) = one all-or-nothing batch: any conflict aborts it, none is written.
	// `atomic: false` opts into PARTIAL apply — valid items land, each refused item (a stale
	// baseline included) comes back in `conflicts[]` with its own reason. A comment's parentId must
	// address an ALREADY-ACTIVE comment (an intra-batch forward reference is not expressible), so
	// nothing cascades here: every item is independent. A rejected CREATE has no id yet, so its
	// conflict is keyed by the item's POSITION ("#0", "#1", …).
	Task<CommentBatchResult> UpsertAsync(
		string projectKey, string board, IReadOnlyList<CommentItem> items, bool atomic = true, CancellationToken ct = default);

	// THE comment read verb (list = search without a query). Without `query`: a deterministic
	// chronological listing of the active comments (optionally scoped to one `board` and/or one
	// owner `nodeId` — both already resolved). With `query`: a lexical FTS relevance selection
	// over comment bodies in the same scope. Semantic retrieval is not wired for comments yet,
	// so a query degrades to the lexical floor (Retrievers reports semantic=false, degraded=false).
	Task<CommentSearchResult> SearchAsync(
		string projectKey, string? board, string? nodeId, string? query, int limit, CancellationToken ct = default);

	// Comments added/updated/removed on a board since a `sinceVersion` cursor (no writes) —
	// mirrors ITasksService.DeltaAsync. The comment version cursor is per-board (the upsert
	// batch partitions by Board), so a caller passes the board's comment `currentVersion`.
	Task<CommentDelta> DeltaAsync(
		string projectKey, string board, long sinceVersion, CancellationToken ct = default);

	// One active comment by its (project-unique) id, or null when it is missing/deleted — the
	// addressed single read that completes the uniform-entity matrix (mirrors memory_get).
	Task<CommentView?> GetAsync(string projectKey, string id, CancellationToken ct = default);

	// Add a comment under a node. parentId (a comment Key) makes it a reply; it must be an
	// active comment under the SAME (board, nodeId), else the call is rejected. New Key.
	// Retained as the low-ceremony single-write door the board UI uses (comments_upsert is the
	// MCP batch verb); the UI inline editor calls this + EditAsync directly.
	Task<CommentUpsertResult> AddAsync(
		string projectKey, string board, string nodeId, string? parentId, string author, string body,
		IReadOnlyList<string>? tags, CancellationToken ct = default);

	// Edit a comment's body (and, if `tags` is non-null, replace its tag set). `version` is
	// the revision the caller last saw — a stale baseline yields a conflict, not a clobber.
	Task<CommentUpsertResult> EditAsync(
		string projectKey, string board, string id, string body,
		IReadOnlyList<string>? tags, long version, CancellationToken ct = default);

	// Soft-close a comment (and its tags). REJECTED if it still has active replies — the
	// caller must delete the children first. Returns false if it was already gone.
	Task<bool> DeleteAsync(string projectKey, string board, string id, CancellationToken ct = default);

	// The whole thread under one node: a FLAT list of active comments (each carrying its
	// parentId + tags), chronological. The caller builds the tree from parentId.
	Task<IReadOnlyList<CommentView>> ListForNodeAsync(
		string projectKey, string board, string nodeId, CancellationToken ct = default);

	// Every active comment on a board, grouped by owning NodeId — ONE pass for the board UI
	// (no per-node N+1). Mirrors TagStore.BoardTagsAsync.
	Task<ILookup<string, CommentView>> ListForBoardAsync(
		string projectKey, string board, CancellationToken ct = default);
}

// A comment as seen by callers (MCP/UI) — the Data row stays internal to the service.
public sealed record CommentView(
	string Id,
	string NodeId,
	string? ParentId,
	string Author,
	string Body,
	IReadOnlyList<string> Tags,
	long Version,
	DateTime Created,
	DateTime Updated);

// Outcome of add/edit, mirroring the temporal upsert result shape used by sessions/tasks.
public sealed record CommentUpsertResult(
	bool Applied,
	long CurrentVersion,
	string? Id,
	IReadOnlyList<CommentConflict> Conflicts);

public sealed record CommentConflict(string Id, string Kind, long BaselineVersion, long? ActiveVersion, string? Reason = null);

// One item of a comments_upsert batch. Id null/empty ⇒ CREATE (NodeId is the RESOLVED 32-hex
// owner, Author required, ParentId optional = reply); Id present ⇒ PATCH body/tags of that
// comment under the `Version` watermark. Tags: null = leave as-is on an edit (a create with
// null tags starts empty), a non-null list REPLACES the set.
public sealed record CommentItem(
	string? Id,
	string? NodeId,
	string? ParentId,
	string? Author,
	string Body,
	IReadOnlyList<string>? Tags,
	long Version);

// Outcome of a comments_upsert batch, mirroring the tasks_upsert ack: `Applied` is the single
// source of truth (false ⇒ nothing written, `Conflicts` explains every rejected id); on success
// `Added`/`Updated` carry the created/edited comments (with their tags + assigned version).
public sealed record CommentBatchResult(
	bool Applied,
	long CurrentVersion,
	IReadOnlyList<CommentView> Added,
	IReadOnlyList<CommentView> Updated,
	IReadOnlyList<CommentConflict> Conflicts);

// A comments_search answer: the selected comments plus honest retrieval provenance (null in a
// deterministic listing; in query mode it reports the lexical floor — semantic is not wired yet).
public sealed record CommentSearchResult(
	IReadOnlyList<CommentView> Items,
	PetBox.Core.Search.SearchRetrievers? Retrievers = null);

// A comments_delta answer: the comments added/updated/removed on a board since a version cursor,
// plus the board's current comment cursor to advance to (mirrors the tasks delta split).
public sealed record CommentDelta(
	long CurrentVersion,
	IReadOnlyList<CommentView> Added,
	IReadOnlyList<CommentView> Updated,
	IReadOnlyList<string> Removed);
