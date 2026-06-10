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

public sealed record CommentsListResult(IReadOnlyList<CommentView> Comments);

public sealed record CommentDeleteResult(bool Deleted);

// ---- config.* ------------------------------------------------------------------------

public sealed record ConfigBindingCreatedResult(long Id, string Path, string Tags, string Kind);

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

// Read/echo projection of a memory entry for the list/upsert/delta MCP surface. `Body` is
// snippet/slice-controlled (null -> omitted). Mirrors the old anonymous EntryDto/Project shapes.
public sealed record MemoryEntryRow(
	string Key,
	string Type,
	string? Description,
	string? Body,
	string? Tags,
	long Version,
	string? Metadata);

public sealed record MemoryListResult(IReadOnlyList<MemoryEntryRow> Entries);

// Provenance of a hybrid search/recall: which retrievers ran and whether the answer is degraded.
public sealed record RetrieverInfo(bool Lexical, bool Semantic, bool Degraded);

public sealed record MemorySearchResultView(IReadOnlyList<MemoryEntryRow> Entries, RetrieverInfo Retrievers);

// memory.upsert / memory.delta echo (mirrors the old anonymous Serialize shape).
public sealed record MemoryConflictView(string Key, string Kind, long BaselineVersion, long? ActiveVersion);

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

// One recall hit, labelled by scope (project|workspace) and store.
public sealed record MemoryRecallHit(
	string Scope,
	string Store,
	string Key,
	string Type,
	string Description,
	string? Body,
	string Tags);

public sealed record MemoryRecallResult(IReadOnlyList<MemoryRecallHit> Results, RetrieverInfo Retrievers);

// ---- relations.* ---------------------------------------------------------------------

public sealed record RelationCreatedResult(string Id, string Kind, string FromNodeId, string ToNodeId);

public sealed record RelationRow(string Id, string Kind, string FromNodeId, string ToNodeId, DateTime CreatedAt, DateTime? ClosedAt);

public sealed record RelationsListResult(IReadOnlyList<RelationRow> Relations);

public sealed record RelationDeletedResult(bool Deleted);

// ---- report.issue --------------------------------------------------------------------

public sealed record ReportIssueResult(bool Reported, string Project, string Board, string Key);

// ---- session.* -----------------------------------------------------------------------

public sealed record SessionConflictView(string Key, string Kind);

public sealed record SessionUpsertResult(bool Applied, long CurrentVersion, IReadOnlyList<SessionConflictView> Conflicts);

public sealed record SessionGetResult(string SessionId, string Agent, string Content, int Length, long Version);

public sealed record SessionRowView(string SessionId, string Agent, long Version);

public sealed record SessionListResult(IReadOnlyList<SessionRowView> Sessions);

// ---- tasks.* (board lifecycle + workflow; node-shaped results reuse Tasks.Contract) ---

public sealed record BoardCreatedResult(string ProjectKey, string Name, string Kind, string? Description, string? SpecBoard, DateTime CreatedAt);

public sealed record BoardSetSpecResult(bool Set, string? SpecBoard);

public sealed record BoardRow(string Name, string Kind, string? Description, string? SpecBoard, DateTime CreatedAt, bool Closed);

public sealed record BoardListResult(IReadOnlyList<BoardRow> Boards);

public sealed record BoardDeletedResult(bool Deleted);

public sealed record BoardClosedResult(bool Closed);

public sealed record BoardReopenedResult(bool Reopened);

// tasks.search wire: a board-aware compact projection of an enriched node + retriever provenance.
public sealed record TaskSearchNodeView(
	string Key,
	string NodeId,
	string Board,
	string? ParentSlug,
	int Depth,
	string Status,
	string Type,
	string Title,
	string? Body,
	long Priority,
	string? Delivery,
	IReadOnlyList<LinkDto>? Spec,
	IReadOnlyList<LinkDto>? BlockedBy,
	IReadOnlyList<LinkDto>? LinkedTasks,
	IReadOnlyList<LinkDto>? Supersedes,
	IReadOnlyList<string> Tags,
	string? Url);

public sealed record TaskSearchResultView(IReadOnlyList<TaskSearchNodeView> Nodes, RetrieverInfo Retrievers);

// tasks.workflow wire shape (board kind + per-type statuses/transitions catalog).
public sealed record WorkflowStatusView(string Slug, string Name, string Kind);

public sealed record WorkflowTransitionView(string From, string To, bool RequiresApproval, bool RequiresReason);

public sealed record WorkflowTypeView(
	string Type,
	string Initial,
	IReadOnlyList<WorkflowStatusView> Statuses,
	IReadOnlyList<WorkflowTransitionView> Transitions);

public sealed record WorkflowView(string Kind, IReadOnlyList<WorkflowTypeView> Types);
