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

// `DefaultProject` is the key's fallback project for tools whose projectKey is optional — set
// only on a cross-project ("*") key that carries one (omitted from the wire when null).
public sealed record WhoAmIResult(string? Project, IReadOnlyList<string> Scopes, string? DefaultProject = null);

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
//   • `Conflicts` carries no CAS conflict — a PUT-by-key cannot have one. It is empty on an ATOMIC
//     call (a validation failure throws and aborts the whole batch). Under `atomic:false` it is
//     where a REJECTED item lands, one entry per item, with the reason — the same promise as the
//     other batch verbs, with the watermark half of it simply having no subject here.
public sealed record ConfigBindingsUpsertResult(
	bool Applied,
	long CurrentVersion,
	IReadOnlyList<ConfigBindingRow> Added,
	IReadOnlyList<ConfigBindingRow> Updated,
	IReadOnlyList<long> Superseded,
	IReadOnlyList<ConfigBindingConflict> Conflicts);

// One binding item the batch refused (partial mode only). Config bindings are immutable rows
// keyed by (path, tagset) with no version watermark, so `Kind` is always "Rejected" — there is
// no Stale to report. The shape still mirrors the other verbs' conflicts[]: WHICH entry, and WHY.
public sealed record ConfigBindingConflict(string Path, string Tags, string Kind, string Reason);

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

// `Sandbox` (spec work/smoke-writes-into-real-projects) marks a project as the containment target
// for sandbox-only API keys — see ApiKeyCreatedResult.SandboxOnly.
public sealed record ProjectCreatedResult(string Key, string WorkspaceKey, string? Name, string? Description, bool Sandbox = false);

public sealed record ProjectRow(string Key, string WorkspaceKey, string Name, string Description, bool Sandbox = false);

public sealed record ProjectListResult(IReadOnlyList<ProjectRow> Projects);

// ---- apikey.* (provisioning; replaces entity.* type "apikey") -------------------------

// apikey_create returns the raw key ONCE (it is never retrievable again) + its granted scopes.
// `DefaultProjectKey` is the cross-project key's fallback project (null on a project-scoped key,
// which already defaults to its own claim). `SandboxOnly` (spec work/smoke-writes-into-real-projects)
// marks the key unable to write anywhere except a Project.Sandbox = true project — see
// ProjectScope.AuthorizesAsync.
public sealed record ApiKeyCreatedResult(string Key, string ProjectKey, IReadOnlyList<string> Scopes, DateTime? ExpiresAt,
	string? DefaultProjectKey = null, bool SandboxOnly = false);

// `LastUsedAt` (spec apikey-last-used) is the MERGED value: the later of the stored column and the
// in-memory stamp, so a call made seconds ago is visible NOW rather than after the next flush.
// NULL = never used (distinguishable from used-long-ago, which is the point of the field).
public sealed record ApiKeyRow(string Key, string Name, string Scopes, DateTime CreatedAt, DateTime? ExpiresAt,
	string? DefaultProjectKey = null, bool SandboxOnly = false, DateTime? LastUsedAt = null);

public sealed record ApiKeyListResult(IReadOnlyList<ApiKeyRow> Keys);

// apikey_update patches an ISSUED key in place — the secret is unchanged (and is the address, not a
// result). `Updated` names the fields this call actually touched, so a caller can tell a real patch
// from a no-op: an omitted field is left alone, it is NOT rewritten with a default.
public sealed record ApiKeyUpdatedResult(string Key, string ProjectKey, IReadOnlyList<string> Scopes, DateTime? ExpiresAt,
	string? DefaultProjectKey, bool SandboxOnly, IReadOnlyList<string> Updated);

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

// RetentionDays is the log's OWN override (spec log-retention-cascade) — null means the log has
// none and is swept by the project/workspace/system cascade.
public sealed record LogCreatedResult(string Name, string? Description, DateTime CreatedAt, int? RetentionDays = null);

public sealed record LogRow(string Name, string? Description, DateTime CreatedAt, DateTime UpdatedAt, int? RetentionDays = null);

public sealed record LogListResult(IReadOnlyList<LogRow> Logs);

// log_update patches ONLY the retention override today — RetentionDays null means it was just
// cleared (0 on the wire), reverting the log to the cascade.
public sealed record LogUpdatedResult(string Name, int? RetentionDays);

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
//
// The impression counters (surfaced/opened) are kept for back-compat but they cannot tell
// "dear and off-target" from "cheap and dead-on" — they count that a row appeared, not what it
// cost or whether it fit. The COST/FIT pair does (spec: usage-cost-and-fit-separate), over the
// trailing `WindowDays`: DeliveredChars/RowChars = the context this store spent, AvgKRel = the
// event-weighted mean fit of what it spent it on. Additive: null on a store with no deliveries
// in the window (and on any client that never asked).
public sealed record MemoryStoreUsageRow(
	int TotalEntries,
	int SurfacedAtLeastOnce,
	int OpenedAtLeastOnce,
	double SurfacedFraction,
	double OpenedFraction,
	DateTime? MedianLastHitAt,
	int DeadCount,
	IReadOnlyList<string> DeadTailKeys,
	int? WindowDays = null,
	long? Deliveries = null,
	long? DeliveredChars = null,
	long? RowChars = null,
	double? AvgKRel = null,
	int? EntriesDelivered = null);

public sealed record MemoryStoreListResult(IReadOnlyList<MemoryStoreRow> Stores);

public sealed record MemoryStoreDeletedResult(bool Deleted);

// memory_get result (spec addressed-read-batched): ALWAYS a list, whether the caller addressed
// one `key` or a batch of `keys` — one shape for both, so a client never branches on arity.
// Rows come back in the requested key order; a key that resolved to nothing is simply absent
// (the batch is a soft filter, exactly like tasks_search `keys[]`).
public sealed record MemoryGetResultView(IReadOnlyList<PetBox.Memory.Contract.MemoryEntryView> Entries);

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

// Provenance of a hybrid search/recall: which retrievers ran, whether the answer is degraded and
// WHY (spec: search-provenance). `DegradedReason` is a stable machine code — see
// PetBox.Core.Search.SearchDegradedReason: embed-no-route | embed-upstream-4xx | embed-transient |
// embed-rate-limited | index-error. Additive/optional: omitted (null) whenever nothing degraded, so
// old clients are untouched, while a new one can tell a permanent CONFIG hole ("this project has no
// embed route, semantic search is dead here") from a passing blip — instead of a mute degraded:true.
//
// `SemanticLag` (spec search-semantic-lag) is the vector leg's coverage trail — docs the async
// worker has not embedded yet (0 = fully drained); null when no semantic leg answered. It stops
// `semantic:true` reading as "coverage complete" after a reindex/outage. `Reranked` (spec
// search-degraded-provenance) is laid in NOW so switching the deferred reranker on is not a contract
// change; today it is always false (no rerank pass runs yet).
public sealed record RetrieverInfo(bool Lexical, bool Semantic, bool Degraded, string? DegradedReason = null,
	long? SemanticLag = null, bool Reranked = false);

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
	string? Retriever = null,
	// The entry's own cost/fit, from delivery_events (includeUsage only; spec:
	// usage-cost-and-fit-separate). DeliveredChars = all-time body chars this entry has poured
	// into callers' context; AvgKRel = the mean within-request fit of those deliveries (null =
	// it has only ever been delivered by a listing, which runs no relevance leg). This is the
	// ONLY read surface of delivery_events per entry: surfaced/opened say an entry keeps
	// APPEARING, these two say what that costs and whether it was worth it.
	long? DeliveredChars = null,
	double? AvgKRel = null);

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

// One row of a relations_create batch (and the historical single-create shape).
public sealed record RelationCreatedResult(string Id, string Kind, string FromNodeId, string ToNodeId);

// Batch create result — Relations is always present (length 1 for the single-form BC path).
public sealed record RelationsCreatedResult(IReadOnlyList<RelationCreatedResult> Relations);

public sealed record RelationRow(string Id, string Kind, string FromNodeId, string ToNodeId, DateTime CreatedAt, DateTime? ClosedAt);

public sealed record RelationsListResult(IReadOnlyList<RelationRow> Relations);

// One row of a relations_delete batch (id + whether soft-close found an active edge).
public sealed record RelationDeletedResult(string Id, bool Deleted);

// Batch delete result — Relations is always present (length 1 for the single-id BC path).
public sealed record RelationsDeletedResult(IReadOnlyList<RelationDeletedResult> Relations);

// ---- report_issue --------------------------------------------------------------------

public sealed record ReportIssueResult(bool Reported, string Project, string Board, string Key);

// ---- session.* -----------------------------------------------------------------------

public sealed record SessionUpsertResult(string SessionId, long Version, int MessageCount);

// session_append: Applied=false + Reason="gap" is the STRUCTURED contiguity reject —
// LastOrdinal is the server's cursor, the client resends the tail from LastOrdinal+1.
public sealed record SessionAppendResult(string SessionId, bool Applied, long LastOrdinal, int Appended, string? Reason);

// Meta is the optional observed client stamp (raw JSON object string) when present.
public sealed record SessionGetResult(string SessionId, string Agent, string Content, int Length, long Version, string? Meta = null);

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

public sealed record BoardCreatedResult(string ProjectKey, string Name, string Kind, string? Description, string? SpecBoard, DateTime CreatedAt, string? MethodologyInstance = null);

public sealed record BoardSetSpecResult(bool Set, string? SpecBoard);

public sealed record BoardRow(string Name, string Kind, string? Description, string? SpecBoard, DateTime CreatedAt, bool Closed, string? MethodologyInstance = null);

public sealed record BoardListResult(IReadOnlyList<BoardRow> Boards);

public sealed record BoardAdoptResult(string Name, string Kind, string? MethodologyInstance);

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
//   listing/query  → `EffectiveStatusKind`, the statusKind facet that ACTUALLY applied — echoed
//                    verbatim from TasksSearchDocs.ResolveStatusKindFacet (spec
//                    search-echo-effective-statuskind-filter), so a defaulted visibility (no
//                    statusKind passed) is OBSERVABLE instead of silent: default query →
//                    [open,terminalok], default listing → [open], explicit statusKind → echoed
//                    resolved set, includeClosed:true → null (NEUTRAL, no facet applied — every
//                    kind). null on the groupBy tag-projection branch (no rows selected by facet).
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
	string? Hint = null,
	IReadOnlyList<string>? EffectiveStatusKind = null);

// tasks_workflow wire shape (board kind + statuses/transitions catalog, grouped by FSM).
public sealed record WorkflowStatusView(string Slug, string Name, string Kind, string? Description = null);

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

// Legacy singleton-definition wire shapes (admin editor dual-read + MethodologyWire
// ProjectDefinition). Public MCP verbs for def_*/enable are gone — use template_* and
// create/list/get/close + rules_* instead. These records remain for the dual-read path.
public sealed record MethodologyDefUpsertResult(
	long Version, bool Changed, int Migrated = 0, int BoardsOnKinds = 0, string? Hint = null);

public sealed record MethodologyDefDeleteResult(bool Deleted, long Version);

// Wire document shape shared by MethodologyWire.ProjectDefinition (admin + dual-read).
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
	IReadOnlyList<MethodologyTagAxisView>? TagAxes = null,
	// Mirrors MethodologyDefinition.StrictMode (spec methodology-gate-strictness). Default false.
	bool StrictMode = false);

// ---- methodology templates (methodology-template-storage) ----------------------------

// tasks_methodology_template_upsert / _snapshot / _delete ack.
public sealed record MethodologyTemplateUpsertResult(string Key, long Version, bool Changed);

// tasks_methodology_template_delete ack (Deleted mirrors Changed for the delete verb).
public sealed record MethodologyTemplateDeleteResult(string Key, bool Deleted, long Version);

// tasks_methodology_template_get answer. Found=true → key/source + the template document
// (kinds/workflows). Found=false → honest miss (not an error) for a non-builtin key that has
// no stored template and is not the dual-read legacy key. Source ∈ stored|builtin|definition.
public sealed record MethodologyTemplateGetResult(
	bool Found,
	string? Key = null,
	string? Source = null,
	string? Name = null,
	IReadOnlyList<MethodologyKindView>? Kinds = null,
	long? Version = null,
	DateTime? Created = null,
	DateTime? Updated = null,
	IReadOnlyList<MethodologyLinkKindView>? LinkKinds = null,
	IReadOnlyList<MethodologyTagAxisView>? TagAxes = null,
	bool StrictMode = false);

// tasks_methodology_template_list answer: builtins + stored (+ dual-read definition entry).
public sealed record MethodologyTemplateListResult(IReadOnlyList<MethodologyTemplateListItemView> Templates);

public sealed record MethodologyTemplateListItemView(
	string Key, string Source, string Name, long Version, DateTime? Updated = null);

// ---- methodology instances (methodology-instance-core) --------------------------------

public sealed record MethodologyInstanceBoardView(string Name, string Kind, bool Closed, string? SpecBoard = null);

public sealed record MethodologyInstanceViewResult(
	string Name,
	bool Closed,
	long Version,
	DateTime Created,
	DateTime Updated,
	DateTime? ClosedAt,
	string DefinitionName,
	IReadOnlyList<string> Kinds,
	IReadOnlyList<MethodologyInstanceBoardView> Boards,
	IReadOnlyDictionary<string, int> Counts);

public sealed record MethodologyInstanceCreateResult(
	string Name, bool Changed, bool Closed, long Version,
	IReadOnlyList<MethodologyInstanceBoardView> Boards);

public sealed record MethodologyInstanceCloseResult(
	string Name, bool Changed, bool Closed, long Version,
	IReadOnlyList<MethodologyInstanceBoardView> Boards);

public sealed record MethodologyInstanceListResult(IReadOnlyList<MethodologyInstanceViewResult> Instances);

public sealed record MethodologyInstanceGetResult(
	bool Found,
	string? Name = null,
	MethodologyInstanceViewResult? Instance = null);

// tasks_methodology_active_get / tasks_methodology_set_active (methodology-active-instance):
// the project's explicit "which instance is active" pointer. Name is null when unset.
public sealed record MethodologyActiveGetResult(string? Name, long Version);

public sealed record MethodologyActiveSetResult(string? Name, bool Changed, long Version);

// tasks_methodology_rules_get: Found=true → name + full rules document (same kinds/workflows
// shape as template_get) + version baseline for rules_upsert. Found=false on miss.
public sealed record MethodologyInstanceRulesGetResult(
	bool Found,
	string? Name = null,
	bool? Closed = null,
	string? DefinitionName = null,
	IReadOnlyList<MethodologyKindView>? Kinds = null,
	long? Version = null,
	DateTime? Created = null,
	DateTime? Updated = null,
	IReadOnlyList<MethodologyLinkKindView>? LinkKinds = null,
	IReadOnlyList<MethodologyTagAxisView>? TagAxes = null,
	bool StrictMode = false);

// tasks_methodology_rules_upsert ack: version cursor, whether a revision was written, and
// how many live member-board nodes the migration rewrote.
public sealed record MethodologyInstanceRulesUpsertResult(
	string Name, long Version, bool Changed, int Migrated = 0);

// tasks_methodology_utility_get: Found=true → the project's utility-layer document (same
// kinds/workflows shape as rules_get/template_get) + version baseline for utility_upsert.
// Found=false when the project has never defined one (everything resolves from presets).
// No Name/Closed fields — the utility layer is a project-level singleton, not a named,
// closeable instance.
public sealed record MethodologyUtilityGetResult(
	bool Found,
	string? DefinitionName = null,
	IReadOnlyList<MethodologyKindView>? Kinds = null,
	long? Version = null,
	DateTime? Created = null,
	DateTime? Updated = null,
	IReadOnlyList<MethodologyLinkKindView>? LinkKinds = null,
	IReadOnlyList<MethodologyTagAxisView>? TagAxes = null);

// tasks_methodology_utility_upsert ack: version cursor, whether a revision was written, and
// how many live utility-homed nodes the migration rewrote.
public sealed record MethodologyUtilityUpsertResult(long Version, bool Changed, int Migrated = 0);

// tasks_methodology_describe ack (spec methodology-describe-verb): the natural-key-addressed
// primitive was found and its Description replaced; `version` is the instance rules cursor
// AFTER the write (a fresh baseline for rules_upsert, same field as rules_upsert's own ack —
// this verb still writes through the whole document internally, it just never asks the
// caller to supply it or its version).
public sealed record MethodologyDescribeResult(string Name, string Primitive, long Version);

// One kind of a stored methodology definition; workflow blocks reuse the tasks_workflow
// status vocabulary (kind = open|terminalok|terminalcancel). LinkConstraints are the
// kind's per-type creation link requirements, Effects its declared transition effects
// (null = none declared, omitted by the serializer).
//
// MUST mirror MethodologyKindDef (PetBox.Tasks.Workflow) FIELD FOR FIELD — same parity
// obligation as MethodologyKindInput (see its note): rules_get/template_get feed the
// STANDARD rules_upsert/template_upsert read-edit-write cycle, so a domain field this view
// omits is invisible to a caller building the next upsert from this output, and gets wiped
// on the very next honest edit (work/mcp-rules-get-is-lossy-so-the-round-trip-still-
// destroys — AutoWireSpecFrom/Delivery/DefaultView/OutlineReveal were missing here even
// after the INPUT side already carried them). Add a domain field → add it here too, or the
// {Def, View} half of MethodologyKindContractParityTests goes red.
public sealed record MethodologyKindView(
	string Kind, bool QuickAddAllowed, IReadOnlyList<MethodologyWorkflowBlockView> Workflows,
	IReadOnlyList<MethodologyLinkConstraintView>? LinkConstraints = null,
	IReadOnlyList<MethodologyEffectView>? Effects = null,
	string? AutoWireSpecFrom = null,
	MethodologyDeliveryView? Delivery = null,
	string? DefaultView = null,
	string? OutlineReveal = null,
	bool? Singleton = null,
	MethodologyBlocksGateView? BlocksGate = null,
	string? Description = null,
	string? BoardName = null);

// Mirrors MethodologyBlocksGateDef 1:1 — the output-side counterpart of MethodologyBlocksGateInput.
public sealed record MethodologyBlocksGateView(string Status, string ReleaseTo);

// Mirrors MethodologyDeliveryDef 1:1 — the output-side counterpart of MethodologyDeliveryInput.
public sealed record MethodologyDeliveryView(IReadOnlyList<string> RequiredTypes, IReadOnlyList<string> DefectTypes, string Link);

// "A new <type> on this kind's boards must carry a <link> at creation" (link =
// task_spec|blocks|idea_spec — the upsert-expressible kinds). `targetKind`/
// `targetStatuses` declare what the link must point at (null = no restriction, omitted).
public sealed record MethodologyLinkConstraintView(
	string Type, string Link,
	string? TargetKind = null, IReadOnlyList<string>? TargetStatuses = null,
	string? Description = null);

// One declared transition effect: on entering (default) or leaving (`onLeave`, Effect.onLeave)
// `on`, `direction` `link` nodes are set to `set` (`onlyFrom` = only linked nodes currently in
// that status; null = any, omitted). `set` null/omitted = a pure edge-consumption effect.
public sealed record MethodologyEffectView(
	string On, string Link, string Direction, string? Set, string? OnlyFrom = null, bool OnLeave = false,
	string? Description = null);

// A project-declared relation kind (free semantic edge, no FSM effects). `category` is the
// camelCase string neutral|process; `direction` is the stored-edge orientation (null = none).
public sealed record MethodologyLinkKindView(
	string Slug, string? Description = null,
	string? Category = null,
	MethodologyLinkDirectionView? Direction = null);

// The stored-edge orientation of a declared relation kind (mirrors MethodologyLinkDirectionDef):
// fromKind/toKind constrain the node kind at each end of relations.from→to; null = unconstrained.
public sealed record MethodologyLinkDirectionView(
	string? FromKind = null, string? ToKind = null, string? Label = null);

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
//
// `requiresReason`/`preconditionArtifact`/`enforceApproval` are the LEGACY shape (output-side
// counterpart of MethodologyTransitionInput's legacy fields — see its note). `requiredArtifacts`/
// `enforce` are the schema-v2 replacement (spec methodology-gate-strictness); null = declared via
// the legacy fields instead (or no gate).
public sealed record MethodologyTransitionView(
	string From, string To, bool RequiresApproval, bool RequiresReason, string? PreconditionArtifact = null,
	bool EnforceApproval = false, IReadOnlyList<string>? Checklist = null, string? Description = null,
	IReadOnlyList<MethodologyRequiredArtifactView>? RequiredArtifacts = null,
	MethodologyGateEnforcementView? Enforce = null);

// Mirrors RequiredArtifactDef 1:1 — the output-side counterpart of MethodologyRequiredArtifactInput.
public sealed record MethodologyRequiredArtifactView(string Slug, bool Inline = false);

// Mirrors GateEnforcementDef 1:1 — the output-side counterpart of MethodologyGateEnforcementInput.
public sealed record MethodologyGateEnforcementView(bool? Approval = null, bool? Artifacts = null);

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

// ---- agent_def_* (portable agent-definition store) -----------------------------------

public sealed record AgentDefListResult(IReadOnlyList<AgentDefListItemView> Definitions);
public sealed record AgentDefListItemView(string Key, string Name, long Version, DateTime Updated);

public sealed record AgentDefGetResult(
	bool Found,
	string? Key = null,
	string? Name = null,
	IReadOnlyList<AgentDefRoleView>? Roles = null,
	long? Version = null,
	DateTime? Created = null,
	DateTime? Updated = null);

public sealed record AgentDefRoleView(
	string Slug,
	string Tier,
	IReadOnlyList<string> RequiredCapabilities,
	AgentDefSpawnView? Spawn = null,
	AgentDefEscalationView? Escalation = null,
	string? Notes = null);

public sealed record AgentDefSpawnView(bool Allowed, IReadOnlyList<string>? AllowedRoles = null);
public sealed record AgentDefEscalationView(bool Available, IReadOnlyList<string>? Targets = null);

public sealed record AgentDefUpsertResult(string Key, long Version, bool Changed);
public sealed record AgentDefDeleteResult(string Key, bool Deleted, long Version);
