using PetBox.Tasks.Contract;

namespace PetBox.Web.Mcp.Contract;

// Typed MCP tool-output records (typed-surface Phase 3). Every *.Tools success payload
// returns one of these concrete records instead of an anonymous object, so the MCP SDK can
// derive an outputSchema (via [McpServerTool(UseStructuredContent = true, OutputSchemaType =
// typeof(...))]). The wire JSON is UNCHANGED: McpJsonUtilities.DefaultOptions camelCases the
// PascalCase properties and omits nulls (WhenWritingNull), so e.g. `Applied` -> "applied" and
// a null field is dropped — identical to the old hand-written anonymous keys.
//
// Records that mirror an existing module Contract shape REUSE it (CommentUpsertResult,
// EmbedResult, MethodologyView, …). These web-only records cover shapes that the MCP tool
// composes itself (wrappers, inline anonymous objects, MCP-specific projections).

// ---- whoami --------------------------------------------------------------------------

public sealed record WhoAmIResult(string? Project, IReadOnlyList<string> Scopes);

// ---- comments.* ----------------------------------------------------------------------

// Truncated/Omitted/Hint are the response-budget markers (spec bounded-result-sets): filled
// only when the rows were prefix-cut against the output budget — an in-budget answer
// serializes byte-identical to the old shape (nulls are omitted). Same pattern on every
// list result below.
public sealed record CommentsListResult(IReadOnlyList<CommentView> Comments,
	bool? Truncated = null, int? Omitted = null, string? Hint = null);

public sealed record CommentDeleteResult(bool Deleted);

// ---- config.* ------------------------------------------------------------------------

// `Superseded` = ids of previously-active bindings with the identical (path, normalized
// tagset) that this upsert soft-closed (PUT-by-(path,tagset) semantics; empty = plain create).
public sealed record ConfigBindingUpsertResult(long Id, string Path, string Tags, string Kind, IReadOnlyList<long> Superseded);

public sealed record ConfigBindingRow(long Id, string Path, string Tags, string Kind);

public sealed record ConfigBindingsListResult(IReadOnlyList<ConfigBindingRow> Bindings);

public sealed record ConfigBindingDeletedResult(bool Deleted, long Id);

// ---- project.* (provisioning; replaces entity.* type "project") ----------------------

public sealed record ProjectCreatedResult(string Key, string WorkspaceKey, string? Name, string? Description);

public sealed record ProjectRow(string Key, string WorkspaceKey, string Name, string Description);

public sealed record ProjectListResult(IReadOnlyList<ProjectRow> Projects);

// ---- apikey.* (provisioning; replaces entity.* type "apikey") -------------------------

// apikey.create returns the raw key ONCE (it is never retrievable again) + its granted scopes.
public sealed record ApiKeyCreatedResult(string Key, string ProjectKey, IReadOnlyList<string> Scopes, DateTime? ExpiresAt);

public sealed record ApiKeyRow(string Key, string Name, string Scopes, DateTime CreatedAt, DateTime? ExpiresAt);

public sealed record ApiKeyListResult(IReadOnlyList<ApiKeyRow> Keys);

public sealed record ApiKeyDeletedResult(bool Deleted, string Key);

// ---- data.* --------------------------------------------------------------------------

public sealed record DataSchemaApplyResult(string Kind, string Hash, string? ExistingHash, string? Error);

// db lifecycle (replaces entity.* type "db"): create/list/delete/describe.
public sealed record DataDbCreatedResult(string Name, string? Description, long MaxPageCount, DateTime CreatedAt);

public sealed record DataDbRow(string Name, string? Description, long MaxPageCount, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record DataDbListResult(IReadOnlyList<DataDbRow> Dbs);

public sealed record DataDbDeletedResult(bool Deleted, string Name);

public sealed record DataColumnView(string Name, string Type, bool NotNull, bool Pk);

public sealed record DataTableView(string Name, IReadOnlyList<DataColumnView> Columns);

public sealed record DataDbDescribeResult(IReadOnlyList<DataTableView> Tables);

// data.query is intrinsically dynamic: rows are an open list of column->value maps.
public sealed record DataQueryResult(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

public sealed record DataExecResult(int Affected);

// ---- llm.* ---------------------------------------------------------------------------

public sealed record LlmConfigSetResult(bool Ok, int Endpoints, int Routes);

// ---- log.* lifecycle (replaces entity.* type "log") ----------------------------------

public sealed record LogCreatedResult(string Name, string? Description, DateTime CreatedAt);

public sealed record LogRow(string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record LogListResult(IReadOnlyList<LogRow> Logs);

public sealed record LogDeletedResult(bool Deleted, string Name);

// ---- log.query -----------------------------------------------------------------------

// A single log event as projected onto the MCP wire (timestamp pre-formatted, level
// stringified, properties JSON-stringified per-value). Null fields are omitted.
public sealed record LogEventView(
	long Id,
	string ServiceKey,
	string Timestamp,
	string Level,
	string? Message,
	string? MessageTemplate,
	string? Exception,
	IReadOnlyDictionary<string, object?> Properties);

// log.query is a discriminated union over `Kind`: "events" (Count + Events set; the table
// fields null/omitted) or "table" (Columns + Rows set; the events fields null/omitted). One
// record carries both arms; null-omission keeps each arm's wire identical to the old anonymous
// objects. Rows are an open table (cells are arbitrary scalars).
public sealed record LogQueryResultView(
	string Kind,
	int? Count = null,
	IReadOnlyList<LogEventView>? Events = null,
	IReadOnlyList<string>? Columns = null,
	IReadOnlyList<IReadOnlyList<object?>>? Rows = null);

// ---- memory.* ------------------------------------------------------------------------

public sealed record MemoryStoreCreatedResult(string ProjectKey, string Name, string? Description, DateTime CreatedAt);

public sealed record MemoryStoreRow(string Name, string? Description, DateTime CreatedAt);

public sealed record MemoryStoreListResult(IReadOnlyList<MemoryStoreRow> Stores);

public sealed record MemoryStoreDeletedResult(bool Deleted);

// Echo projection of a memory entry for the upsert/delta MCP surface. `Body` is
// slice-controlled (null -> omitted). `Tags` is an array (the memory surface speaks
// tag arrays; storage stays CSV).
public sealed record MemoryEntryRow(
	string Key,
	string Type,
	string? Description,
	string? Body,
	IReadOnlyList<string> Tags,
	long Version,
	string? Metadata);

// Provenance of a hybrid search/recall: which retrievers ran and whether the answer is degraded.
public sealed record RetrieverInfo(bool Lexical, bool Semantic, bool Degraded);

// memory.upsert / memory.delta echo (mirrors the old anonymous Serialize shape).
public sealed record MemoryConflictView(string Key, string Kind, long BaselineVersion, long? ActiveVersion, string? Reason = null);

public sealed record MemoryUpsertResultView(
	bool Applied,
	long CurrentVersion,
	int Inserted,
	int Closed,
	IReadOnlyList<MemoryConflictView> Conflicts,
	IReadOnlyList<MemoryEntryRow> Added,
	IReadOnlyList<MemoryEntryRow> Updated,
	IReadOnlyList<string> Removed);

public sealed record MemoryRememberResult(string Id, string Scope, string Store, string Key);

// One memory.search row, labelled by scope (project|workspace) and store. Carries Version so
// a search → upsert edit has its per-key CAS baseline without an extra get (or a
// guaranteed-Stale 0). Usage fields appear only under `includeUsage:true` (null -> omitted)
// — spec: memory-usage-observability.
public sealed record MemorySearchHitView(
	string Scope,
	string Store,
	string Key,
	string Type,
	string Description,
	string? Body,
	IReadOnlyList<string> Tags,
	long Version,
	long? Surfaced = null,
	long? Opened = null,
	DateTime? LastHitAt = null);

// The memory.search result — ONE shape for both modes (SearchEnvelope form): `Items` in
// final order, `Retrievers` provenance with a query (null in listing mode), and the
// response-budget markers Truncated/Omitted/Hint (null = complete).
public sealed record MemorySearchResultView(
	IReadOnlyList<MemorySearchHitView> Items,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null,
	int? Omitted = null,
	string? Hint = null);

// ---- relations.* ---------------------------------------------------------------------

public sealed record RelationCreatedResult(string Id, string Kind, string FromNodeId, string ToNodeId);

public sealed record RelationRow(string Id, string Kind, string FromNodeId, string ToNodeId, DateTime CreatedAt, DateTime? ClosedAt);

public sealed record RelationsListResult(IReadOnlyList<RelationRow> Relations);

public sealed record RelationDeletedResult(bool Deleted);

// ---- report.issue --------------------------------------------------------------------

public sealed record ReportIssueResult(bool Reported, string Project, string Board, string Key);

// ---- session.* -----------------------------------------------------------------------

public sealed record SessionUpsertResult(string SessionId, long Version, int MessageCount);

// session.append: Applied=false + Reason="gap" is the STRUCTURED contiguity reject —
// LastOrdinal is the server's cursor, the client resends the tail from LastOrdinal+1.
public sealed record SessionAppendResult(string SessionId, bool Applied, long LastOrdinal, int Appended, string? Reason);

public sealed record SessionGetResult(string SessionId, string Agent, string Content, int Length, long Version);

public sealed record SessionDeletedResult(bool Deleted, string SessionId);

// One episodic hit inside a discovered session; Message is the ordinal to feed back
// into session.get (the provenance bridge).
public sealed record SessionSearchHitView(long Message, string Role, string Snippet, double Score, string? Retriever);

// One session.search item — the union of the verb's two modes (list = search without q):
//   listing row → SessionId/Agent/Version (the former session.list row; query fields null);
//   query row   → SessionId/Agent + Description (the digest), episodic `Hits` and the
//                 per-session `Retrievers` (Version null — a discovery is digest-based).
// Null fields are omitted on the wire, so each mode serializes without the other's arm.
public sealed record SessionSearchItemView(
	string SessionId,
	string Agent,
	long? Version = null,
	string? Description = null,
	IReadOnlyList<SessionSearchHitView>? Hits = null,
	RetrieverInfo? Retrievers = null);

// The session.search result — ONE shape for both modes (SearchEnvelope form): `Items` in
// final order plus the response-budget markers (null = complete). With a query it also
// carries `Retrievers` (the STAGE-1 discovery provenance; per-session provenance rides
// each item) and `Distilled`/`Reason` — false + a machine-readable code (e.g.
// "no-digest-store") when the project has no digest store yet (not "no matches"); all
// three are null/omitted in listing mode.
public sealed record SessionSearchResultView(
	IReadOnlyList<SessionSearchItemView> Items,
	bool? Distilled = null,
	string? Reason = null,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null,
	int? Omitted = null,
	string? Hint = null);

// ---- tasks.* (board lifecycle + workflow; node-shaped results reuse Tasks.Contract) ---

public sealed record BoardCreatedResult(string ProjectKey, string Name, string Kind, string? Description, string? SpecBoard, DateTime CreatedAt);

public sealed record BoardSetSpecResult(bool Set, string? SpecBoard);

public sealed record BoardRow(string Name, string Kind, string? Description, string? SpecBoard, DateTime CreatedAt, bool Closed);

public sealed record BoardListResult(IReadOnlyList<BoardRow> Boards);

public sealed record BoardDeletedResult(bool Deleted);

public sealed record BoardClosedResult(bool Closed);

public sealed record BoardReopenedResult(bool Reopened);

// tasks.search wire row: a board-aware projection of an enriched node (rows may span
// boards, so each carries `Board`). Tree navigation rides ParentNodeId/ParentSlug/Depth
// (the part_of projection); null fields are omitted on the wire.
public sealed record TaskSearchNodeView(
	string Key,
	string NodeId,
	string Board,
	string? ParentNodeId,
	string? ParentSlug,
	int Depth,
	string Status,
	string Type,
	string Title,
	string Body,
	string? CommitRef,
	long Priority,
	string? Delivery,
	IReadOnlyList<LinkDto>? Spec,
	IReadOnlyList<LinkDto>? BlockedBy,
	IReadOnlyList<LinkDto>? LinkedTasks,
	IReadOnlyList<LinkDto>? Supersedes,
	IReadOnlyList<string>? RenamedFrom,
	IReadOnlyList<string> Tags,
	long Version,
	string? Url);

// The tasks.search result — ONE shape for every mode (a single OutputSchemaType):
//   listing/query  → `Nodes` (final order), plus board context (Board/Kind/SpecBoard/
//                    CurrentVersion) when the read was board-scoped;
//   query          → `Retrievers` provenance (null in listing mode);
//   groupBy        → `GroupBy`+`Groups` (the tag projection; `Nodes` empty);
//   any            → the response-budget markers Truncated/Omitted/Hint (null = complete).
public sealed record TaskSearchResultView(
	IReadOnlyList<TaskSearchNodeView> Nodes,
	string? Board = null,
	string? Kind = null,
	string? SpecBoard = null,
	long? CurrentVersion = null,
	IReadOnlyList<string>? GroupBy = null,
	IReadOnlyList<TagGroup>? Groups = null,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null,
	int? Omitted = null,
	string? Hint = null);

// tasks.workflow wire shape (board kind + statuses/transitions catalog, grouped by FSM).
public sealed record WorkflowStatusView(string Slug, string Name, string Kind);

// `PreconditionArtifact` names a comment-artifact tag the node must carry before the
// transition fires — filled for definition-resolved kinds, null (omitted by the
// serializer) for the catalog presets.
public sealed record WorkflowTransitionView(string From, string To, bool RequiresApproval, bool RequiresReason, string? PreconditionArtifact = null);

// One state machine shared by every type slug in `Types` — types with an identical FSM are
// grouped into a single block (feature=bug=chore on a work board is ONE block, not three
// copies of the same statuses/transitions).
public sealed record WorkflowGroupView(
	IReadOnlyList<string> Types,
	string Initial,
	IReadOnlyList<WorkflowStatusView> Statuses,
	IReadOnlyList<WorkflowTransitionView> Transitions);

public sealed record WorkflowView(string Kind, IReadOnlyList<WorkflowGroupView> Workflows);

// tasks.methodology_def_upsert ack: the definition's current revision number (the baseline
// for the next edit), whether this call created a new revision (false = an identical
// resubmit collapsed to a no-op), and how many live nodes the declared `migration` rewrote
// onto the new resolution (0 = nothing needed repair). A version conflict throws (the error
// envelope names the current version), so this shape never carries conflicts.
public sealed record MethodologyDefUpsertResult(long Version, bool Changed, int Migrated = 0);

// tasks.methodology_def_get answer. Defined=true → the stored definition (name/kinds) plus
// its revision metadata. Defined=false → the project has no definition and runs on the
// built-in preset named in `Preset` (the structured "not defined" shape, mirroring
// session.search's distilled:false + reason — an honest state, not an error or a miss).
public sealed record MethodologyDefGetResult(
	bool Defined,
	string? Preset = null,
	string? Name = null,
	IReadOnlyList<MethodologyKindView>? Kinds = null,
	long? Version = null,
	DateTime? Created = null,
	DateTime? Updated = null,
	// Definition-level primitives (null = none declared, omitted by the serializer):
	// project-declared relation kinds and tag axes.
	IReadOnlyList<MethodologyLinkKindView>? LinkKinds = null,
	IReadOnlyList<MethodologyTagAxisView>? TagAxes = null);

// One kind of a stored methodology definition; workflow blocks reuse the tasks.workflow
// status vocabulary (kind = open|terminalok|terminalcancel). LinkConstraints are the
// kind's per-type creation link requirements (null = none declared).
public sealed record MethodologyKindView(
	string Kind, bool QuickAddAllowed, IReadOnlyList<MethodologyWorkflowBlockView> Workflows,
	IReadOnlyList<MethodologyLinkConstraintView>? LinkConstraints = null);

// "A new <type> on this kind's boards must carry a <link> at creation" (link =
// task_spec|blocks|idea_spec — the upsert-expressible kinds).
public sealed record MethodologyLinkConstraintView(string Type, string Link);

// A project-declared relation kind (free semantic edge, no FSM effects).
public sealed record MethodologyLinkKindView(string Slug, string? Description = null);

// A declared tag namespace for definition-resolved boards.
public sealed record MethodologyTagAxisView(string Namespace, string? Description = null);

public sealed record MethodologyWorkflowBlockView(
	IReadOnlyList<string> Types,
	string Initial,
	IReadOnlyList<WorkflowStatusView> Statuses,
	IReadOnlyList<MethodologyTransitionView> Transitions);

// WorkflowTransitionView plus the definition-only `preconditionArtifact` (a comment-artifact
// tag required before the transition; null = omitted by the serializer).
public sealed record MethodologyTransitionView(
	string From, string To, bool RequiresApproval, bool RequiresReason, string? PreconditionArtifact = null);
