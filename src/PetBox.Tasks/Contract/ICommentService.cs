namespace PetBox.Tasks.Contract;

// The single door to node comments — a generic, editable, tree-structured comment thread
// under any plan node, on any board. Deliberately SEPARATE from ITasksService: comments
// are not PlanNodes, so keeping them off the tasks door guarantees they never leak into
// tasks.get / the workflow FSM / delivery roll-ups. The Web layer (MCP tools + the board
// page) depends only on this interface (TasksBoundaryTests forbids touching the store/db).
public interface ICommentService
{
	// Add a comment under a node. parentId (a comment Key) makes it a reply; it must be an
	// active comment under the SAME (board, nodeId), else the call is rejected. New Key.
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

public sealed record CommentConflict(string Id, string Kind, long BaselineVersion, long? ActiveVersion);
