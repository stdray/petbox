namespace PetBox.Web.Pages.Shared;

// spec node-session-provenance-visible-in-ui: the session(s) a node's provenance traces to,
// shared by _NodeSessionProvenanceBadge.cshtml across every caller (board card, table row,
// kanban card, outline chips, node detail page) the same way ObservationSignalModel shares the
// recurrence/regression rendering rule — one partial, one place the "how do we show this" answer
// lives. `OriginSessionId` is TaskNodeView's write-once field ("" = none ever recorded — a
// permanent property of the node, never backfilled); `OriginSessions` is the accumulating union
// of every session that has since touched the node (null on a caller that doesn't resolve it —
// e.g. a lean/query-mode projection — treated the same as empty by the partial). WorkspaceKey/
// ProjectKey are needed to route each session id through Routes.ProjectSession.
public sealed record NodeSessionProvenanceModel(
	string OriginSessionId, IReadOnlyList<string>? OriginSessions, string WorkspaceKey, string ProjectKey);
