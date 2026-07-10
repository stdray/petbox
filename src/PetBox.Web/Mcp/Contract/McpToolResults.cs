using System.Text.Json.Serialization;
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

// comments_upsert / comments_delta echo — mirrors the tasks_upsert ack ({applied, currentVersion,
// added/updated/removed, conflicts}). `Applied` is the single source of truth (false ⇒ nothing
// written, `Conflicts` explains each rejected id). `Removed` is used by comments_delta (empty on
// an upsert — deletes go through comments_delete). CommentView/CommentConflict come from the Tasks
// contract (reused, like memory reuses its own views).
public sealed record CommentsUpsertResult(
	bool Applied,
	long CurrentVersion,
	IReadOnlyList<CommentView> Added,
	IReadOnlyList<CommentView> Updated,
	IReadOnlyList<string> Removed,
	IReadOnlyList<CommentConflict> Conflicts);

// comments_search answer (list = search without a query). `Retrievers` is present only in query
// mode (the lexical floor — semantic isn't wired for comments yet). Truncated/Omitted/Hint are the
// response-budget markers (null/omitted on an in-budget answer).
public sealed record CommentsSearchResult(
	IReadOnlyList<CommentView> Items,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null, int? Omitted = null, string? Hint = null);

public sealed record CommentDeleteResult(bool Deleted);

// ---- config.* ------------------------------------------------------------------------

public sealed record ConfigBindingRow(long Id, string Path, string Tags, string Kind);

// config_binding_upsert / config_binding_delta echo — the uniform-entity-verbs batch envelope,
// adapted to the config store's model. NOTE the deliberate deviations from the tasks/memory/comments
// envelope (config bindings are NOT temporally watermarked — see the tool docs):
//   • `CurrentVersion` is the store's MAX binding Id (the auto-increment identity is the store-wide
//     monotonic cursor; there is no per-row Version watermark — Version is always 1). Pass it to
//     config_binding_delta as `sinceVersion`.
//   • A write is PUT-by-(path, tagset): `Added` = items that created a fresh (path, tagset);
//     `Updated` = items that superseded an active twin (a NEW immutable row replaced it).
//   • `Superseded` = the soft-closed twin ids (kept for the PUT-by semantics visibility).
//   • `Conflicts` exists only for shape parity — a PUT-by-key never has a CAS conflict, so it is
//     ALWAYS empty (a validation failure throws and aborts the whole atomic batch instead).
public sealed record ConfigBindingsUpsertResult(
	bool Applied,
	long CurrentVersion,
	IReadOnlyList<ConfigBindingRow> Added,
	IReadOnlyList<ConfigBindingRow> Updated,
	IReadOnlyList<long> Superseded,
	IReadOnlyList<string> Conflicts);

// config_binding_search answer (list = search without a query). `Retrievers` is present only in
// query mode — config has no FTS/vector index, so a query is a server-side substring match over
// path/tags/plaintext-value and reports the lexical floor (semantic:false, degraded:false). Secret
// values are never returned (rows carry id/path/tags/kind only), so there is no body/bodyLen knob;
// the output budget still applies (Truncated/Omitted/Hint when the rows overflow).
public sealed record ConfigBindingsSearchResult(
	IReadOnlyList<ConfigBindingRow> Bindings,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null, int? Omitted = null, string? Hint = null);

public sealed record ConfigBindingDeletedResult(bool Deleted, long Id);

// ---- project.* (provisioning; replaces entity.* type "project") ----------------------

public sealed record ProjectCreatedResult(string Key, string WorkspaceKey, string? Name, string? Description);

public sealed record ProjectRow(string Key, string WorkspaceKey, string Name, string Description);

public sealed record ProjectListResult(IReadOnlyList<ProjectRow> Projects);

// ---- apikey.* (provisioning; replaces entity.* type "apikey") -------------------------

// apikey_create returns the raw key ONCE (it is never retrievable again) + its granted scopes.
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

// data_query is intrinsically dynamic: rows are an open list of column->value maps.
public sealed record DataQueryResult(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

public sealed record DataExecResult(int Affected);

// ---- llm.* ---------------------------------------------------------------------------

public sealed record LlmConfigSetResult(bool Ok, int Endpoints, int Routes);

// ---- log.* lifecycle (replaces entity.* type "log") ----------------------------------

public sealed record LogCreatedResult(string Name, string? Description, DateTime CreatedAt);

public sealed record LogRow(string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record LogListResult(IReadOnlyList<LogRow> Logs);

public sealed record LogDeletedResult(bool Deleted, string Name);

// ---- log_query -----------------------------------------------------------------------

// A single log event as projected onto the MCP wire (timestamp pre-formatted, level
// stringified, properties JSON-stringified per-value). Null fields are omitted.
//
// Field names are pinned to the PascalCase KQL schema (Id, ServiceKey, Timestamp, Level…),
// mirroring LogEventDto on the REST side. McpJsonUtilities.DefaultOptions camelCases by
// default, but the table arm (LogQueryResultView.Columns) carries the schema names verbatim
// as data — so without these pins the two log_query shapes disagree on casing (event.timestamp
// vs column "Timestamp") and an agent parser written for the table shape breaks on the events
// shape. Kept identical to the KQL schema and the REST DTO so every shape uses one casing.
public sealed record LogEventView(
	[property: JsonPropertyName("Id")] long Id,
	[property: JsonPropertyName("ServiceKey")] string ServiceKey,
	[property: JsonPropertyName("Timestamp")] string Timestamp,
	[property: JsonPropertyName("Level")] string Level,
	[property: JsonPropertyName("Message")] string? Message,
	[property: JsonPropertyName("MessageTemplate")] string? MessageTemplate,
	[property: JsonPropertyName("Exception")] string? Exception,
	[property: JsonPropertyName("Properties")] IReadOnlyDictionary<string, object?> Properties);

// log_query is a discriminated union over `Kind`: "events" (Count + Events set; the table
// fields null/omitted) or "table" (Columns + Rows set; the events fields null/omitted). One
// record carries both arms; null-omission keeps each arm's wire identical to the old anonymous
// objects. Rows are an open table (cells are arbitrary scalars). Truncated (either arm): the
// result was cut by the service's row cap (KqlLimits); true when cut, omitted otherwise.
public sealed record LogQueryResultView(
	string Kind,
	int? Count = null,
	IReadOnlyList<LogEventView>? Events = null,
	IReadOnlyList<string>? Columns = null,
	IReadOnlyList<IReadOnlyList<object?>>? Rows = null,
	bool? Truncated = null);

// ---- memory.* ------------------------------------------------------------------------

public sealed record MemoryStoreCreatedResult(string ProjectKey, string Name, string? Description, DateTime CreatedAt);

public sealed record MemoryStoreRow(string Scope, string Name, string? Description, DateTime CreatedAt, MemoryStoreUsageRow? Usage = null);

// Per-store usage aggregate on the wire (memory_store_list includeUsage:true; null when
// the flag is off). Flattens MemoryUsageAggregate.DeadTail into DeadCount + DeadTailKeys —
// spec: memory-usage-aggregate.
public sealed record MemoryStoreUsageRow(
	int TotalEntries,
	int SurfacedAtLeastOnce,
	int OpenedAtLeastOnce,
	double SurfacedFraction,
	double OpenedFraction,
	DateTime? MedianLastHitAt,
	int DeadCount,
	IReadOnlyList<string> DeadTailKeys);

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

// memory_upsert / memory_delta echo (mirrors the old anonymous Serialize shape).
// ChangedFields (Stale only): THIS entry's payload fields that moved past the author's
// baseline — the informed-retry surface, entity-scoped by construction.
public sealed record MemoryConflictView(
	string Key, string Kind, long BaselineVersion, long? ActiveVersion, string? Reason = null,
	IReadOnlyList<string>? ChangedFields = null);

// AutoResolved: keys whose stale baseline was accepted because the entry's payload had not
// semantically moved since the author's read (bookkeeping bumps only) — applied + reported.
public sealed record MemoryUpsertResultView(
	bool Applied,
	long CurrentVersion,
	int Inserted,
	int Closed,
	IReadOnlyList<MemoryConflictView> Conflicts,
	IReadOnlyList<MemoryEntryRow> Added,
	IReadOnlyList<MemoryEntryRow> Updated,
	IReadOnlyList<string> Removed,
	IReadOnlyList<string> AutoResolved);

public sealed record MemoryRememberResult(string Id, string Scope, string Store, string Key);

// One memory_search row, labelled by scope (project|workspace) and store. Carries Version so
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
	DateTime? LastHitAt = null,
	// Distinct source-session count (provenance width) — a compact number, null when the fact
	// carries no session provenance (spec memoverhaul-provenance-surface).
	int? SourcesCount = null,
	// Per-row relevance provenance (spec search-row-provenance): query mode only — Score is the
	// fused, freshness-blended relevance, Retriever names how the hit surfaced ("lexical" =
	// lexically confirmed, "semantic" = vector-only); both null and omitted on the wire in
	// listing mode.
	double? Score = null,
	string? Retriever = null);

// The memory_search result — ONE shape for both modes (SearchEnvelope form): `Items` in
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

// ---- report_issue --------------------------------------------------------------------

public sealed record ReportIssueResult(bool Reported, string Project, string Board, string Key);

// ---- session.* -----------------------------------------------------------------------

public sealed record SessionUpsertResult(string SessionId, long Version, int MessageCount);

// session_append: Applied=false + Reason="gap" is the STRUCTURED contiguity reject —
// LastOrdinal is the server's cursor, the client resends the tail from LastOrdinal+1.
public sealed record SessionAppendResult(string SessionId, bool Applied, long LastOrdinal, int Appended, string? Reason);

public sealed record SessionGetResult(string SessionId, string Agent, string Content, int Length, long Version);

public sealed record SessionDeletedResult(bool Deleted, string SessionId);

// One episodic hit inside a discovered session; Message is the ordinal to feed back
// into session_get (the provenance bridge).
public sealed record SessionSearchHitView(long Message, string Role, string Snippet, double Score, string? Retriever);

// One session_search item — the union of the verb's two modes (list = search without q):
//   listing row → SessionId/Agent/Version (the former session.list row; query fields null);
//   query row   → SessionId/Agent + Description (the digest), episodic `Hits` and the
//                 per-session `Retrievers` (Version null — a discovery is digest-based).
// Null fields are omitted on the wire, so each mode serializes without the other's arm.
// `Sources` (query mode only) names which stage-1 discovery leg(s) raised this session:
// "digest" (the LLM summary), "term" (verbatim full-text over the raw transcript, spec
// session-discovery-verbatim), "fullscan" (opt-in raw-substring scan, spec
// session-fullscan-optin) — a session can carry more than one when several legs agree.
public sealed record SessionSearchItemView(
	string SessionId,
	string Agent,
	long? Version = null,
	string? Description = null,
	IReadOnlyList<SessionSearchHitView>? Hits = null,
	RetrieverInfo? Retrievers = null,
	IReadOnlyList<string>? Sources = null);

// The session_search result — ONE shape for both modes (SearchEnvelope form): `Items` in
// final order plus the response-budget markers (null = complete). With a query it also
// carries `Retrievers` (the STAGE-1 discovery provenance; per-session provenance rides
// each item) and `Distilled`/`Reason` — false + a machine-readable code (e.g.
// "no-digest-store") when the project has no digest store yet (not "no matches"); all
// three are null/omitted in listing mode.
//
// FullScan* (spec: session-fullscan-optin) are null unless `fullScan:true` was passed.
// Once requested: FullScanRan=false + FullScanReason="not-allowed" means the two-key
// permission setting denied it (asked, but not run — never silent); FullScanRan=true +
// FullScanCapped=true means it ran but the project holds more sessions than the scan cap
// (also logged server-side).
public sealed record SessionSearchResultView(
	IReadOnlyList<SessionSearchItemView> Items,
	bool? Distilled = null,
	string? Reason = null,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null,
	int? Omitted = null,
	string? Hint = null,
	bool? FullScanRequested = null,
	bool? FullScanRan = null,
	string? FullScanReason = null,
	bool? FullScanCapped = null);

// session_delta echo — the sessions family's catch-up surface, kept uniform with the other _delta
// verbs (a store cursor + the rows changed since it + the response-budget markers). Sessions are
// last-write-wins BLOBS with no store-wide version watermark, so:
//   • `CurrentVersion` is the newest session's `Updated` time as Unix epoch MILLISECONDS — the real
//     monotonic field the archive tracks (each per-session `Version` is only that session's message
//     ordinal, not a global cursor). Pass it back as the next `sinceVersion`.
//   • `Items` = the active sessions whose Updated-ms > sinceVersion (LWW blobs → a flat "changed
//     since" list; no added/updated split, and a soft-DELETE is not surfaced as removed).
// `Items` reuses SessionSearchItemView (SessionId/Agent/Version — the listing arm) for family shape.
public sealed record SessionDeltaResult(
	long CurrentVersion,
	IReadOnlyList<SessionSearchItemView> Items,
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

// tasks_search wire row: a board-aware projection of an enriched node (rows may span
// boards, so each carries `Board`). Tree navigation rides ParentNodeId/ParentSlug/Depth
// (the part_of projection); null fields are omitted on the wire. Score/Retriever carry the
// per-row relevance provenance (spec search-row-provenance): query mode only (Score is the
// fused rank-based RRF value, Retriever names how the hit surfaced —
// "lexical"|"semantic"|"exact"); both null and omitted on the wire in listing mode.
// QUERY-mode rows are LEAN (spec search-lean-rows): a relevance row carries only what picks
// the entity — identity/title/snippet/status/tags/version + score/retriever; the enrichment
// (parent/depth/delivery/spec/links/commits/priority) is nulled → omitted on the wire and
// rides listing mode or tasks_node_get. Depth/Priority/Commits are therefore NULLABLE so
// they can be dropped in query mode; listing mode always fills them.
public sealed record TaskSearchNodeView(
	string Key,
	string NodeId,
	string Board,
	string? ParentNodeId,
	string? ParentSlug,
	int? Depth,
	string Status,
	string Type,
	string Title,
	string? Body, // uniform bodyLen contract: ~240 snippet default, full at -1, omitted (null) at 0
	IReadOnlyList<string>? Commits,
	long? Priority,
	string? Delivery,
	IReadOnlyList<LinkDto>? Spec,
	IReadOnlyList<LinkDto>? BlockedBy,
	IReadOnlyList<LinkDto>? LinkedTasks,
	IReadOnlyList<LinkDto>? Supersedes,
	IReadOnlyList<string>? RenamedFrom,
	IReadOnlyList<string> Tags,
	long Version,
	string? Url,
	double? Score = null,
	string? Retriever = null,
	// "comment" when the row surfaced because a COMMENT under this node matched the query
	// (tasks-search-comments); null when the node itself matched. Relevance provenance, so it
	// survives the lean q-mode cut like Score/Retriever.
	string? MatchedIn = null);

// The tasks_search result — ONE shape for every mode (a single OutputSchemaType):
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

// tasks_workflow wire shape (board kind + statuses/transitions catalog, grouped by FSM).
public sealed record WorkflowStatusView(string Slug, string Name, string Kind);

// `PreconditionArtifact` names a comment-artifact tag the node must carry before the
// transition fires — filled for definition-resolved kinds, null (omitted by the
// serializer) for the catalog presets. `EnforceApproval` is the approval-gate MODE: true
// means the server BLOCKS the transition unless the actor can approve; false keeps
// owner-only by convention (the builtin presets never enforce).
public sealed record WorkflowTransitionView(string From, string To, bool RequiresApproval, bool RequiresReason, bool EnforceApproval, string? PreconditionArtifact = null);

// One state machine shared by every type slug in `Types` — types with an identical FSM are
// grouped into a single block (feature=bug=chore on a work board is ONE block, not three
// copies of the same statuses/transitions).
public sealed record WorkflowGroupView(
	IReadOnlyList<string> Types,
	string Initial,
	IReadOnlyList<WorkflowStatusView> Statuses,
	IReadOnlyList<WorkflowTransitionView> Transitions);

public sealed record WorkflowView(string Kind, IReadOnlyList<WorkflowGroupView> Workflows);

// tasks_methodology_def_upsert ack: the definition's current revision number (the baseline
// for the next edit), whether this call created a new revision (false = an identical
// resubmit collapsed to a no-op), and how many live nodes the declared `migration` rewrote
// onto the new resolution (0 = nothing needed repair). A version conflict throws (the error
// envelope names the current version), so this shape never carries conflicts.
//
// Additive surface honesty (def vs enable vs board_create): `BoardsOnKinds` counts open
// boards whose kind is declared in this definition (0 = definition only — nothing provisioned
// yet). `Hint` is non-null only when BoardsOnKinds is 0 (next-step guidance; null omitted).
public sealed record MethodologyDefUpsertResult(
	long Version, bool Changed, int Migrated = 0, int BoardsOnKinds = 0, string? Hint = null);

// tasks_methodology_def_delete ack: whether a definition was actually removed (false = the
// project had none — an idempotent no-op) and the definition cursor after the delete (the
// baseline should the caller re-create one). A version conflict or a live-node
// incompatibility throws instead — nothing is partially deleted.
public sealed record MethodologyDefDeleteResult(bool Deleted, long Version);

// tasks_methodology_def_get answer. Defined=true → the stored definition (name/kinds) plus
// its revision metadata. Defined=false → the project has no definition and runs on the
// built-in preset named in `Preset` (the structured "not defined" shape, mirroring
// session_search's distilled:false + reason — an honest state, not an error or a miss).
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

// One kind of a stored methodology definition; workflow blocks reuse the tasks_workflow
// status vocabulary (kind = open|terminalok|terminalcancel). LinkConstraints are the
// kind's per-type creation link requirements, Effects its declared transition effects
// (null = none declared, omitted by the serializer).
public sealed record MethodologyKindView(
	string Kind, bool QuickAddAllowed, IReadOnlyList<MethodologyWorkflowBlockView> Workflows,
	IReadOnlyList<MethodologyLinkConstraintView>? LinkConstraints = null,
	IReadOnlyList<MethodologyEffectView>? Effects = null);

// "A new <type> on this kind's boards must carry a <link> at creation" (link =
// task_spec|blocks|idea_spec — the upsert-expressible kinds). `targetKind`/
// `targetStatuses` declare what the link must point at (null = no restriction, omitted).
public sealed record MethodologyLinkConstraintView(
	string Type, string Link,
	string? TargetKind = null, IReadOnlyList<string>? TargetStatuses = null);

// One declared transition effect: on entering `on`, `direction` `link` nodes are set to
// `set` (`onlyFrom` = only linked nodes currently in that status; null = any, omitted).
public sealed record MethodologyEffectView(
	string On, string Link, string Direction, string Set, string? OnlyFrom = null);

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
// tag required before the transition; null = omitted by the serializer), `enforceApproval`
// (the approval gate is server-blocked, not convention) and `checklist` (free-text
// conditions; null = none declared, omitted).
public sealed record MethodologyTransitionView(
	string From, string To, bool RequiresApproval, bool RequiresReason, string? PreconditionArtifact = null,
	bool EnforceApproval = false, IReadOnlyList<string>? Checklist = null);

// ---- tool_describe (spec tool-description-economy) -----------------------------------

// The addressed FULL read of a tool's description: tools/list serves a compact head for heavy
// tools, this returns the complete prose (sentinel merged out) plus the tool's in/out JSON schema.
// `InputSchema`/`OutputSchema` are the raw JSON schema TEXT (serialized), not a nested JsonElement:
// a JsonElement field exports as the boolean schema `true` ("any"), and strict MCP clients (Claude
// Code's Zod validator) reject a `true`-valued property in outputSchema — which broke the WHOLE
// tools/list. As a string the property exports as {"type":"string"} and the caller JSON-parses it.
// `OutputSchema` is null (omitted) for tools that advertise none.
public sealed record ToolDescribeResult(
	string Name,
	string? Title,
	string? Description,
	string InputSchema,
	string? OutputSchema);
