using System.Text.Json.Serialization;
using PetBox.Core.Search;
using PetBox.LlmRouter.Contract;
using PetBox.Tasks.Contract;
using PetBox.Web.Search;

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

// ---- comments_* ----------------------------------------------------------------------

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
// `Warning` (card size-warning-not-wired-to-write-verbs, mirroring MemoryUpsertResultView.Warning):
// set only when a comments_upsert call APPLIED and its request body's \uXXXX-escape inflation
// crossed the threshold ModuleMcp.SizeWarningOrNull measures (see ModuleMcp.SizeGuidanceText) —
// independent of size — never on a refused/conflicted call, where Conflicts is already the signal
// to act on. comments_delta never sets it (no write). Null/omitted the rest of the time.
public sealed record CommentsUpsertResult(
	bool Applied,
	long CurrentVersion,
	IReadOnlyList<CommentView> Added,
	IReadOnlyList<CommentView> Updated,
	IReadOnlyList<string> Removed,
	IReadOnlyList<CommentConflict> Conflicts,
	string? Warning = null);

// comments_search answer (list = search without a query). `Retrievers` is present only in query
// mode (the lexical floor — semantic isn't wired for comments yet). Truncated/Omitted/Hint are the
// response-budget markers (null/omitted on an in-budget answer).
public sealed record CommentsSearchResult(
	IReadOnlyList<CommentView> Items,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null, int? Omitted = null, string? Hint = null);

public sealed record CommentDeleteResult(bool Deleted);

// ---- config_* ------------------------------------------------------------------------

public sealed record ConfigBindingRow(long Id, string Path, string Tags, string Kind);

// config_binding_upsert echo — the uniform-entity-verbs batch envelope, adapted to the config
// store's model. NOTE the deliberate deviations from the tasks/memory/comments envelope (config
// bindings are NOT temporally watermarked, and — batch3 — have NO _delta verb: no tombstone, so a
// delta would only ever repeat _search — see the tool docs):
//   • `CurrentVersion` is the store's MAX binding Id (the auto-increment identity is the store-wide
//     monotonic cursor; there is no per-row Version watermark — Version is always 1).
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

// ---- project_* (provisioning; replaces the RETIRED entity.* type "project") ----------------------

// `Sandbox` (spec work/smoke-writes-into-real-projects) marks a project as the containment target
// for sandbox-only API keys — see ApiKeyCreatedResult.SandboxOnly.
public sealed record ProjectCreatedResult(string Key, string WorkspaceKey, string? Name, string? Description, bool Sandbox = false);

public sealed record ProjectRow(string Key, string WorkspaceKey, string Name, string Description, bool Sandbox = false);

public sealed record ProjectListResult(IReadOnlyList<ProjectRow> Projects);

// ---- apikey_* (provisioning; replaces the RETIRED entity.* type "apikey") -------------------------

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

// data_schema_apply's success shape — ONLY the two soft outcomes reach here now: Kind is
// "Applied" (this call wrote it) or "AlreadyApplied" (same name+sql, no-op), Hash the migration's
// on-file hash either way. Kind:'Failed' (bad SQL) and Kind:'Conflict' (same name, different sql)
// used to ride home as fields of THIS successful response, with a caller that only checked
// isError silently missing them — they now throw through the central error envelope instead
// (McpErrorEnvelopeFilter), so there is no ExistingHash/Error field here to carry: a Conflict's
// existingHash/providedHash live in the thrown exception's message, a Failed's reason too.
public sealed record DataSchemaApplyResult(string Kind, string Hash);

// db lifecycle (replaces the RETIRED entity.* type "db"): create/list/delete/describe.
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

// ---- health_search ---------------------------------------------------------------------

// Latest report for one running service (HealthTools.SearchAsync). ReceivedAt is ISO-8601 UTC;
// AgeSeconds is the server-computed age; Stale = AgeSeconds > staleThresholdSeconds. History is
// null (omitted by the serializer) unless the caller asked for it; null Name/Version/Sha are
// likewise omitted. Moved here from Mcp/HealthTools.cs (resharper-clt-move-wire-records): that
// file also holds the live SearchAsync tool method, so a point [PublicAPI] used to be the only
// safe suppression mechanism there — here the directory-wide glob (root .editorconfig) covers
// NotAccessedPositionalProperty.Global/UnusedAutoPropertyAccessor.Global on its own.
public sealed record HealthServiceView(
	string Svc,
	string? Name,
	IReadOnlyDictionary<string, string> Tags,
	string Status,
	string? Version,
	string? Sha,
	string ReceivedAt,
	long AgeSeconds,
	bool Stale,
	IReadOnlyList<HealthHistoryEntryView>? History = null);

// One historical report for a service, most-recent first. Source is "push" | "pull". Moved here
// alongside HealthServiceView above (same doctrine).
public sealed record HealthHistoryEntryView(
	string Status,
	string? Version,
	string? Sha,
	string ReceivedAt,
	long AgeSeconds,
	string Source);

// ---- llm_* ---------------------------------------------------------------------------

// `Version` is the level's CAS baseline — pass it back as llm_config_upsert's `version`. 0 = the
// level declares nothing yet.
//
// `Level` is the level these rows were read FROM and that an upsert with this projectKey would write
// TO ("System:$" / "Workspace:<workspaceKey>"). It is here because it is NOT derivable from the
// projectKey by anyone but the server: the level comes from the project's WORKSPACE, so ANY project
// of `$system` — the sandbox project `smoke` included, until M048 moved it into its own workspace —
// resolves to the LIVE `System:$` exactly like `$system` does, and the surface used to describe that
// as "this project's own level" (work llm-config-get-level-derivation-trap; this is llm-l5 item 5,
// now decided). Reporting where the caller actually landed is the same contract memory_search keeps
// when it labels every row with the scope it came from.
//
// `ServedBy` is populated ONLY when this level declares nothing and something above it does — the
// level actually serving the project. It answers the question `Version: 0` used to answer wrongly:
// an empty level is not an empty registry, and writing a single row here shadows the inherited one
// WHOLE for every project of this workspace.
public sealed record LlmConfigGetResult(
	IReadOnlyList<LlmEndpoint> Endpoints,
	IReadOnlyList<LlmRoute> Routes,
	long Version,
	string Level,
	string? ServedBy = null);

// `Version` is the level's NEW version after this write — the baseline for the caller's NEXT upsert.
// `Level` is the level that was actually written ("System:$" / "Workspace:<workspaceKey>"), so the
// write reports its own target instead of leaving the caller to re-derive it from the projectKey.
public sealed record LlmConfigSetResult(bool Ok, int Endpoints, int Routes, long Version, string Level);

// ---- log.* lifecycle (replaces the RETIRED entity.* type "log") ----------------------------------

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
// `Hint` (either arm) accompanies a cut the same way the search verbs' Hint does — the fact of a
// cut alone names no action. It is present only when Truncated is true. NO `Omitted` here,
// unlike the search verbs: a row cap has no candidate count behind it, so there is no honest
// number to report — silence beats a fabricated one.
public sealed record LogQueryResultView(
	string Kind,
	int? Count = null,
	IReadOnlyList<LogEventView>? Events = null,
	IReadOnlyList<string>? Columns = null,
	IReadOnlyList<IReadOnlyList<object?>>? Rows = null,
	bool? Truncated = null,
	string? Hint = null);

// ---- memory_* ------------------------------------------------------------------------

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
// (the batch is a soft filter, exactly like tasks_search `nodes[]`).
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
// `semantic:true` reading as "coverage complete" after a reindex/outage. `Ranking` (spec
// search-rerank-in-loop / search-ranking-mode-is-caller-choice) is the tri-state ranking outcome —
// Reranked (the precision path ran), DegradedRrf (Precision was asked for but the rerank path
// couldn't run — a degradation) or ChosenRrf (the caller explicitly asked for Speed — RRF because
// that's what was asked for, never confused with a degradation). Null when no ranking pass applies
// (e.g. a listing, which runs no relevance leg at all). Serialized as a readable string
// (SearchRankingOutcome carries its own JsonStringEnumConverter).
public sealed record RetrieverInfo(bool Lexical, bool Semantic, bool Degraded, string? DegradedReason = null,
	long? SemanticLag = null, SearchRankingOutcome? Ranking = null);

// memory_upsert / memory_delta echo (mirrors the old anonymous Serialize shape).
// ChangedFields (Stale only): THIS entry's payload fields that moved past the author's
// baseline — the informed-retry surface, entity-scoped by construction.
public sealed record MemoryConflictView(
	string Key, string Kind, long BaselineVersion, long? ActiveVersion, string? Reason = null,
	IReadOnlyList<string>? ChangedFields = null);

// AutoResolved: keys whose stale baseline was accepted because the entry's payload had not
// semantically moved since the author's read (bookkeeping bumps only) — applied + reported.
// `Warning` (card mcp-write-degrades-silently-fix, point 4): set only when the call APPLIED
// and its request body's \uXXXX-escape inflation crossed the threshold ModuleMcp.SizeWarningOrNull
// measures (see ModuleMcp.SizeGuidanceText) — independent of size — never on a refused/conflicted
// call, where conflicts[] is already the signal to act on. Null/omitted the rest of the time.
public sealed record MemoryUpsertResultView(
	bool Applied,
	long CurrentVersion,
	int Inserted,
	int Closed,
	IReadOnlyList<MemoryConflictView> Conflicts,
	IReadOnlyList<MemoryEntryRow> Added,
	IReadOnlyList<MemoryEntryRow> Updated,
	IReadOnlyList<string> Removed,
	IReadOnlyList<string> AutoResolved,
	string? Warning = null);

// `Warning` (card mcp-write-degrades-silently-fix) is non-null when the write landed
// DEGRADED in a way the caller could not see otherwise: an empty `description` (the primary
// recall surface — memory_search ranks/shows it, so a factless one is quietly hard to find
// again) or the request body's \uXXXX-escape inflation crossing the threshold
// ModuleMcp.SizeWarningOrNull measures (see ModuleMcp.SizeGuidanceText). Never a refusal — the
// entry is always written when this result is returned; null/omitted when neither applies.
public sealed record MemoryRememberResult(string Id, string Scope, string Store, string Key, string? Warning = null);

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
// PAGINATION (spec: result-set-pageable): `NextCursor` is the opaque keyset resume token, present only
// when rows were withheld. With `q`, `Stop` is ALWAYS present and answers WHY the walk stopped in the
// SAME vocabulary tasks_search and session_search use — "more" | "exhausted" | "pool-boundary" — because
// three read surfaces answering in three shapes is the thing this work exists to prevent. Do not infer
// the end from a missing cursor: "exhausted" and "pool-boundary" both omit it and mean different things
// ("nothing else matched" vs "ranking looked only PoolLimit deep and more matched behind it").
public sealed record MemorySearchResultView(
	IReadOnlyList<MemorySearchHitView> Items,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null,
	int? Omitted = null,
	string? Hint = null,
	string? NextCursor = null,
	string? Stop = null,
	int? PoolLimit = null,
	string? PoolBoundaryHint = null);

// ---- relations_* ---------------------------------------------------------------------

// One row of a relations_create batch (and the historical single-create shape).
public sealed record RelationCreatedResult(string Id, string Kind, string FromNodeId, string ToNodeId);

// One item a relations_create batch refused (uniform-entity-verbs, mirrors CommentConflict/
// UpsertConflictView). Relations carry no version watermark and no natural id at input time
// (kind+from+to, never an id) — so unlike tasks_upsert/comments_upsert there is no Stale/
// BaselineVersion axis here, only domain-guard refusals (bad kind, unresolvable/ambiguous ref).
// Key is always the item's batch POSITION ("#0", "#1", …) — the same convention comments_upsert
// uses for a rejected CREATE, which also has no id yet.
public sealed record RelationConflict(string Key, string Reason);

// Batch create result — Relations is always present (length 1 for the single-form BC path).
// `Applied` is the SINGLE source of truth (mirrors tasks_upsert/comments_upsert): false ⇒
// nothing was written, see Conflicts. Under atomic:true (default) a refusal instead throws
// (unchanged BC — every relations_create failure is a domain-guard refusal, and a domain-guard
// refusal aborts an atomic call as an exception, same as tasks_upsert/comments_upsert do for
// theirs; relations have no concurrency/version axis, so ATOMIC never has to hand back
// applied:false + conflicts the way a Stale conflict would).
public sealed record RelationsCreatedResult(
	bool Applied, IReadOnlyList<RelationCreatedResult> Relations, IReadOnlyList<RelationConflict> Conflicts);

public sealed record RelationRow(string Id, string Kind, string FromNodeId, string ToNodeId, DateTime CreatedAt, DateTime? ClosedAt);

public sealed record RelationsListResult(IReadOnlyList<RelationRow> Relations);

// One row of a relations_delete batch (id + whether soft-close found an active edge).
public sealed record RelationDeletedResult(string Id, bool Deleted);

// Batch delete result — Relations is always present (length 1 for the single-id BC path).
public sealed record RelationsDeletedResult(IReadOnlyList<RelationDeletedResult> Relations);

// ---- search_reindex --------------------------------------------------------------------

// search_reindex's OutputSchemaType (SearchTools.ReindexAsync). Moved here from
// Search/SearchReindexService.cs (resharper-clt-move-wire-records): ProjectKey is populated for
// the remote client's structured content and never read back by local C#
// (ProjectDetail.cshtml.cs's OnPostReindexAsync only reads .Tiers) — the directory-wide glob
// covers NotAccessedPositionalProperty.Global on its own, same as the other records in this file.
// ReindexTierResult stays in SearchReindexService.cs: every one of its properties is read locally
// (LogReindexed), so it never needed a suppression in the first place.
public sealed record SearchReindexResult(string ProjectKey, IReadOnlyList<ReindexTierResult> Tiers)
{
	public long TotalDocsToEmbed => Tiers.Sum(t => t.ActiveDocs);
}

// ---- petbox_report_issue ---------------------------------------------------------------

public sealed record ReportIssueResult(bool Reported, string Project, string Board, string Key);

// ---- session_* -----------------------------------------------------------------------

// `Warning` (card size-warning-not-wired-to-write-verbs, mirroring MemoryRememberResult.Warning):
// session_upsert always writes (no conflict/reject path — it is a last-write-wins snapshot
// replace), so this is set whenever the request body's \uXXXX-escape inflation crosses the
// threshold ModuleMcp.SizeWarningOrNull measures (see ModuleMcp.SizeGuidanceText) — independent
// of size. Never a refusal. Null/omitted the rest of the time.
public sealed record SessionUpsertResult(string SessionId, long Version, int MessageCount, string? Warning = null);

// session_append: Applied=false + Reason="gap" is the STRUCTURED contiguity reject —
// LastOrdinal is the server's cursor, the client resends the tail from LastOrdinal+1.
// `Warning` (card size-warning-not-wired-to-write-verbs, mirroring MemoryUpsertResultView.Warning):
// set only when the call APPLIED and its request body's \uXXXX-escape inflation crossed the
// threshold ModuleMcp.SizeWarningOrNull measures (see ModuleMcp.SizeGuidanceText) — independent
// of size — never on a gap reject, where Reason is already the signal to act on. Null/omitted
// the rest of the time.
public sealed record SessionAppendResult(string SessionId, bool Applied, long LastOrdinal, int Appended, string? Reason, string? Warning = null);

// Meta is the optional observed client stamp (raw JSON object string) when present.
// LastOrdinal (card session-get-from-ordinal) is the ordinal of the LAST message = the
// message count — the cursor for incremental reads, named the way session_append already
// names it (fromOrdinal/lastOrdinal). It is the ORDINAL axis; Length stays the CHAR axis
// (always the full transcript's length, regardless of bodyLen AND of fromOrdinal), so the
// two growth signals never have to be read as one.
public sealed record SessionGetResult(string SessionId, string Agent, string Content, int Length, long Version, long LastOrdinal = 0, string? Meta = null);

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
	bool? FullScanCapped = null,
	// PAGINATION (spec: result-set-pageable), query mode only — the SAME shape tasks_search and
	// memory_search return, because three read surfaces answering in three shapes is what this work
	// exists to prevent. `Stop` is "more" | "exhausted" | "pool-boundary"; do not infer the end from a
	// missing NextCursor, since the last two both omit it and mean different things. `PoolLimit` is the
	// discovery depth the walk may reach. Nothing here names a RANKING MODE: session discovery has no
	// cross-encoder pass, and this contract must not offer a choice it cannot honour.
	string? NextCursor = null,
	string? Stop = null,
	int? PoolLimit = null,
	string? PoolBoundaryHint = null);

// ---- tasks_* (board lifecycle + workflow; node-shaped results reuse Tasks.Contract) ---

public sealed record BoardCreatedResult(string ProjectKey, string Name, string Kind, string? Description, string? WiredBoard, DateTime CreatedAt, string? MethodologyInstance = null);

public sealed record BoardSetWireResult(bool Set, string? WiredBoard);

public sealed record BoardRow(string Name, string Kind, string? Description, string? WiredBoard, DateTime CreatedAt, bool Closed, string? MethodologyInstance = null);

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
//   listing/query  → `Nodes` (final order), plus board context (Board/Kind/WiredBoard/
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
//   any            → `NextCursor`, the keyset resume token (PetBox.Core.Contract.KeysetCursor),
//                    present ONLY when rows were withheld — the budget cut them, or `limit`
//                    capped the page. Issued in BOTH modes now (spec: result-set-pageable): the
//                    relevance order is materialized once into a ranked pool and paged over, so a
//                    query-mode token no longer splices two rankings.
//   q              → `Stop`, `PoolLimit`, `PoolBoundaryHint` — the honesty trio of a paged
//                    relevance walk. `Stop` is ALWAYS present with `q` and answers WHY the walk
//                    stopped in words: "more" (page again with NextCursor), "exhausted" (every
//                    matching row was ranked and served — there is genuinely nothing else), or
//                    "pool-boundary" (ranking looked only `PoolLimit` deep and MORE entities
//                    matched behind it, so these rows are a PREFIX of the match set and no further
//                    page exists to fetch). That last distinction is the entire reason this field
//                    exists rather than leaving the caller to infer the end from a missing cursor:
//                    "we stopped looking" and "there is no more" are different answers, and a
//                    missing cursor cannot tell them apart. `PoolBoundaryHint` carries the
//                    actionable advice for that one case only.
public sealed record TaskSearchResultView(
	IReadOnlyList<TaskSearchNodeView> Nodes,
	string? Board = null,
	string? Kind = null,
	string? WiredBoard = null,
	long? CurrentVersion = null,
	IReadOnlyList<string>? GroupBy = null,
	IReadOnlyList<TagGroup>? Groups = null,
	RetrieverInfo? Retrievers = null,
	bool? Truncated = null,
	int? Omitted = null,
	string? Hint = null,
	IReadOnlyList<string>? EffectiveStatusKind = null,
	string? NextCursor = null,
	string? Stop = null,
	int? PoolLimit = null,
	string? PoolBoundaryHint = null);

// tasks_node_get result (batch 3, mirrors memory_get's addressed-read-batched shape): ALWAYS
// a list, whether the caller addressed one `node` or a batch of `nodes[]` — one shape for
// both arities. A single `node` still fills exactly one row (a miss throws before this type
// is built); a `nodes[]` batch is a SOFT filter — a miss is simply absent, and rows come back
// in the caller's requested order (not dedup/sort order).
public sealed record NodeGetResultView(IReadOnlyList<NodeDetailView> Nodes);

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

// tasks_methodology_template_get answer: key/source + the template document (kinds/workflows).
// An addressed read: a miss (non-builtin key with no stored template and not the dual-read legacy
// key) THROWS — same contract as tasks_node_get. Source ∈ stored|builtin|definition.
//
// NO `found` FIELD (mcp-surface-naming-cleanup wave 5). It used to be here and was ALWAYS true —
// the only branch that could have set it false throws instead. A field that cannot vary teaches a
// caller to test it, and the test can only ever pass; worse, it advertised a second not-found
// dialect the surface does not actually speak. One contract: an addressed read either returns the
// thing or errors.
//
// `Key` is the template's SLUG ADDRESS; `Name` is the document's human-readable prose name. Both
// stay, because here they really are two different things — this pair is the reason the instance
// verbs' `name` had to become `key` (one word was carrying both jobs across the family).
public sealed record MethodologyTemplateGetResult(
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

// A member board of an instance. `Name` here is the BOARD's name and stays `name`: a board is
// addressed by the `board` parameter everywhere on this surface, never by `key`, so renaming it
// would invent a third spelling for a concept that already has one.
public sealed record MethodologyInstanceBoardView(string Name, string Kind, bool Closed, string? WiredBoard = null);

// An instance row/view. `Key` is the instance's SLUG ADDRESS — the exact string every methodology
// verb now takes as its `key` parameter (mcp-surface-naming-cleanup wave 5: it was `name`, which
// on the very same surface ALSO meant a document's display prose — one word, two concepts).
// `DefinitionName` is that display prose and keeps the `name` word, because that is all it is.
// Read `key`, write `key`: the round trip is the point of the rename.
public sealed record MethodologyInstanceViewResult(
	string Key,
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
	string Key, bool Changed, bool Closed, long Version,
	IReadOnlyList<MethodologyInstanceBoardView> Boards);

public sealed record MethodologyInstanceCloseResult(
	string Key, bool Changed, bool Closed, long Version,
	IReadOnlyList<MethodologyInstanceBoardView> Boards);

public sealed record MethodologyInstanceListResult(IReadOnlyList<MethodologyInstanceViewResult> Instances);

// tasks_methodology_get answer. No `found` field (always-true, see MethodologyTemplateGetResult)
// and no top-level key echo — the instance itself carries `key`, and a second copy beside it was
// never populated anyway.
public sealed record MethodologyInstanceGetResult(
	MethodologyInstanceViewResult? Instance = null);

// tasks_methodology_active_get / tasks_methodology_set_active (methodology-active-instance):
// the project's explicit "which instance is active" pointer. `Key` is the pointed-at instance's
// slug address (feed it back to any methodology verb's `key`); null when no pointer is set.
public sealed record MethodologyActiveGetResult(string? Key, long Version);

public sealed record MethodologyActiveSetResult(string? Key, bool Changed, long Version);

// tasks_methodology_rules_get: the instance's `key` + full rules document (same kinds/workflows
// shape as template_get) + version baseline for rules_upsert. An addressed read: a miss THROWS —
// same contract as tasks_node_get, and no `found` field (see MethodologyTemplateGetResult).
// `Key` addresses; `DefinitionName` is the document's display prose.
public sealed record MethodologyInstanceRulesGetResult(
	string? Key = null,
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
	string Key, long Version, bool Changed, int Migrated = 0);

// tasks_methodology_utility_get: the project's utility-layer document (same kinds/workflows shape
// as rules_get/template_get) + version baseline for utility_upsert. An addressed read: THROWS when
// the project has never defined one — same contract as tasks_node_get, and no `found` field (see
// MethodologyTemplateGetResult). No Key/Closed fields either — the utility layer is a
// project-level singleton, not an addressable, closeable instance.
public sealed record MethodologyUtilityGetResult(
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

// tasks_methodology_set_description ack (spec methodology-describe-verb): the natural-key-addressed
// primitive was found and its Description replaced; `version` is the instance rules cursor
// AFTER the write (a fresh baseline for rules_upsert, same field as rules_upsert's own ack —
// this verb still writes through the whole document internally, it just never asks the
// caller to supply it or its version).
public sealed record MethodologySetDescriptionResult(string Key, string Primitive, long Version);

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
// destroys — AutoWireFrom/Delivery/DefaultView/OutlineReveal were missing here even
// after the INPUT side already carried them). Add a domain field → add it here too, or the
// {Def, View} half of MethodologyKindContractParityTests goes red.
public sealed record MethodologyKindView(
	string Kind, bool QuickAddAllowed, IReadOnlyList<MethodologyWorkflowBlockView> Workflows,
	IReadOnlyList<MethodologyLinkConstraintView>? LinkConstraints = null,
	IReadOnlyList<MethodologyEffectView>? Effects = null,
	string? AutoWireFrom = null,
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

// agent_def_get answer. NO `found` field (mcp-surface-naming-cleanup wave 5): this verb used to be
// the ONE addressed read on the surface that answered a miss with found:false while every other one
// — tasks_node_get, tasks_methodology_template_get/_rules_get/_utility_get, the instance get —
// threw. Two dialects for one situation is a thing every caller has to learn twice and one of them
// gets wrong; the miss is now an error here too, naming the key and the project.
public sealed record AgentDefGetResult(
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
