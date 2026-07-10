# Architecture snapshot — PetBox (harvest 2026-07-10)

Companion to `spec-backfill-map.jsonl`. Describes **as-built** module structure, not the requirements catalog.

## PetBox.Core

- **Responsibility:** Shared foundation of the module monolith: tenancy models (Workspace / Project / ApiKey / catalog metas), auth (API-key + cookie/workspace roles, local or remote mode), the main `PetBoxDb` SQLite context + FluentMigrator migrations, temporal-store primitives, hybrid search infrastructure (FTS + vector + RRF), feature-toggle plumbing, and typed settings resolution. Domain modules depend on Core; Core does not own feature-module business rules.
- **Key files:**
  - `src/PetBox.Core/Features/FeatureFlags.cs` — closed feature enum + config gate
  - `src/PetBox.Core/Auth/` — `ApiKeyScopes`, `ProjectScope`, `CompositeApiKeyLookup`, handlers/middleware
  - `src/PetBox.Core/Models/` — Workspace, Project, ApiKey, LogMeta, TaskBoardMeta, MemoryStoreMeta, Health*, Data*, Relation, …
  - `src/PetBox.Core/Data/PetBoxDb.cs`, `MigrationRunner.cs`, `Migrations/`, `Temporal/TemporalStore.cs`
  - `src/PetBox.Core/Search/` — `SearchService`, `ISearchIndex`, FTS/vector indexes, async vectorization
  - `src/PetBox.Core/Settings/` — settings attributes + resolvers
  - `src/PetBox.Core/Services/AgentDefinitionService.cs` — portable agent defs (always-on Core surface)
- **Key invariants:**
  - Feature modules are a closed set; a feature is off unless `Features:<Name>` is explicitly true.
  - ApiKey scopes are an enumerated catalog (no free-form/wildcard scopes); project claim may be `*`.
  - Config-declared API keys win over DB-minted keys on secret collision.
  - `$system` and `$workspace` are reserved projects (undeletable; orphan sweep never touches their files).
  - Search: Synchronous (lexical) indexes update inside the entity transaction; Eventual (vector) indexes never block the write path.
  - Tenancy is Workspace → Project; ConfigBindings are tag records, not FK'd to Project.

## PetBox.Config

- **Responsibility:** Tag-based configuration engine for external consumers (not PetBox self-config). Stores path/value/tags bindings per workspace, resolves by pure tag subset matching (most-specific set wins), encrypts secrets at rest, and exposes REST resolve/CRUD.
- **Key files:**
  - `src/PetBox.Config/ResolvePipeline.cs` — pure resolve
  - `src/PetBox.Config/ConfigApi.cs` — REST surface
  - `src/PetBox.Config/Data/ConfigDb.cs`, `ConfigDbFactory.cs`, `ConfigSchema.cs`
  - `src/PetBox.Config/SecretEncryptor.cs`, `BindingContentHash.cs`, `AutoTagger.cs`
- **Key invariants:**
  - One config SQLite DB per workspace (`config/{workspaceKey}.db`); schema evolution is additive only.
  - Resolve is deterministic and pure (no Project/Service FK joins); equal-specificity ties throw `AmbiguousConfigException` (loud failure, not silent pick).
  - Config write authorizes target workspace from the caller's project claim (or `*`), not the route segment alone.
  - PetBox itself is never configured through this module (`appsettings.json` only).

## PetBox.Log.Core

- **Responsibility:** Log/trace/metric ingestion (CLEF/Seq + OTLP adapters live at the Web boundary), per-(project, logName) SQLite stores, channel-backed ingest pipeline, KQL query engine (Kusto.Language + transform to SQL/DuckDb paths), retention/orphan cleanup, self-log, and anonymous share/TSV export with field masking.
- **Key files:**
  - `src/PetBox.Log.Core/LogApi.cs`, `ShareApi.cs`
  - `src/PetBox.Log.Core/Data/LogStore.cs`, `LogDb.cs`, `LogNames.cs`, `LogSchema.cs`
  - `src/PetBox.Log.Core/Ingestion/ChannelIngestionPipeline.cs`, `CleFParser.cs`
  - `src/PetBox.Log.Core/Query/LogQueryService.cs`, `KqlTransformer*.cs`, `KqlPropertyKeys.cs`
  - `src/PetBox.Log.Core/Retention/RetentionService.cs`, `LogOrphanCleanupService.cs`
  - `src/PetBox.Log.Core/Sharing/ValueMasker.cs`, `FieldMaskingPolicy.cs`
  - `src/PetBox.Log.Core/SelfLogging/SystemLogger*.cs`
- **Key invariants:**
  - Events are append-only (no update path); aging is retention delete only (shipped retention loop is events-only).
  - A named log is `(projectKey, logName)` on disk at `logs/{projectKey}/{logName}.db`; query is always single-log-scoped (no cross-log joins).
  - Property bag keys are normalized at both write (`PropertiesJsonSerializer`) and KQL search boundaries.
  - Reserved self-log is `$system` / `petbox` and cannot be deleted via REST.
  - Path-based ingest authorizes project claim + `logs:ingest` (not bare ApiKey policy alone).
  - `ILogQueryService` is the single KQL orchestration door for REST and MCP.

## PetBox.Data

- **Responsibility:** Per-project user SQLite databases with a raw-SQL pass-through API, DbUp-style named schema scripts, and background WAL checkpoint / orphan file cleanup. PetBox catalogs DBs; pets own table schemas.
- **Key files:**
  - `src/PetBox.Data/DataDbsApi.cs`, `QueryExecApi.cs`, `SchemaApi.cs`, `DataAuth.cs`
  - `src/PetBox.Data/DataDbFactory.cs`
  - `src/PetBox.Data/Contract/IDataSqlService.cs`, `Services/DataSqlService.cs`
  - `src/PetBox.Data/Schema/SchemaRunner.cs`, `SqliteHashingJournal.cs`, `SqlNormalizer.cs`
  - `src/PetBox.Data/WalCheckpointService.cs`, `OrphanCleanupService.cs`
- **Key invariants:**
  - Physical file at `{baseDir}/{projectKey}/{dbName}.db`; create requires project existence in `PetBoxDb.Projects`.
  - PetBox does not CREATE pet tables at DataDb create — schema is applied via `/schema` scripts; journal (`__SchemaVersions`) lives inside the DataDb file.
  - REST and MCP share `IDataSqlService` (PRAGMA deny-list, timeouts, SQLITE_FULL mapping stay single-sourced).
  - Fresh connection per request; WAL checkpoint and orphan cleanup are eventually-consistent BackgroundServices.
  - Cross-project reach is blocked by project-claim authorization (including `*`).

## PetBox.Tasks

- **Responsibility:** Task boards + methodology engine: plan nodes, comments, relations/tags, workflow FSM, methodology definitions/templates/instances, delivery roll-up, and search docs. All boards for a project live in one SQLite file partitioned by Board column.
- **Key files:**
  - `src/PetBox.Tasks/Contract/ITasksService.cs`, `ICommentService.cs`, `TasksActor.cs`, `NodePatch.cs`, `TaskViews.cs`
  - `src/PetBox.Tasks/Services/TasksService.cs`, `CommentService.cs`, `Upsert/*`, `Methodology/*`
  - `src/PetBox.Tasks/Workflow/WorkflowEngine.cs`, `MethodologyPresets.cs`, `MethodologyDefinition.cs`, `Workflow.cs`
  - `src/PetBox.Tasks/Data/TaskBoardStore.cs`, `TasksDb.cs`, `RelationStore.cs`, `PlanNode.cs`
  - `src/PetBox.Tasks/Validation/PlanNodeChangeValidator.cs`, `MethodologyDefinitionValidator.cs`
- **Key invariants:**
  - `ITasksService` is the only domain door (NetArch forbids Web → store/DB direct).
  - `WorkflowEngine` is the single validation point for status/transitions; `TasksActor` carries capability (`CanApprove`), not identity.
  - One file `tasks/<project>.db` for all boards of a project; board names match `^[a-z][a-z0-9_-]{0,99}$`.
  - Closed boards reject writes until reopened; NodeId and type are immutable after set.
  - `part_of` is single-parent + cycle-rejecting; delete of a parent with active children is a soft conflict unless children die in the same batch.
  - Process-role kinds are ≤1 open board per kind **per methodology instance**; classic/simple unlimited.
  - RequiresReason transitions need a first-class non-empty `Reason` (not body merge); approval-gated births need maintainer capability when enforce-approval applies.

## PetBox.Memory

- **Responsibility:** Durable, temporal, searchable agent memory stores per project: typed entries, hybrid search (Class-A FTS in-tx + Class-B async vectors), usage telemetry for agent/human adapters, and the compact `canon` pointer store.
- **Key files:**
  - `src/PetBox.Memory/Contract/IMemoryService.cs`, `IMemoryUsageRecorder.cs`, `MemoryContract.cs`
  - `src/PetBox.Memory/Services/MemoryService.cs`, `MemoryUsageRecorder.cs`, `MemorySearchSource.cs`
  - `src/PetBox.Memory/Data/MemoryStore.cs`, `MemoryDb.cs`, `MemoryEntry.cs`, `MemorySchema.cs`, `Migrations/`
  - `src/PetBox.Memory/Services/LlmClientEmbedder.cs`
- **Key invariants:**
  - Entry `Type` is closed taxonomy: User | Feedback | Project | Reference (distinct from free-form tags).
  - Lexical index updates inside the entity transaction; vectors materialize off the write path (down embedder never blocks/loses writes).
  - Canon store `"canon"` bodies hard-capped at 10_000 chars at the service door (covers upsert and remember).
  - Shared cross-project memory uses reserved container `$workspace` (not `$system`); key-addressed curation is workspace-bound.
  - `IMemoryUsageRecorder` is only for agent/human adapters; internal machine consumers call `IMemoryService` directly.

## PetBox.Sessions

- **Responsibility:** Working-session transcripts (latest snapshot, Brotli JSONL), term/full-scan discovery indexes, and lazy episodic (within-session) search. Gated under the Tasks feature at the host; digests/search orchestration that spans Memory live in Web.
- **Key files:**
  - `src/PetBox.Sessions/Contract/ISessionService.cs`, `ISessionEpisodicIndex.cs`, `SessionSnapshot.cs`, `SessionMessage.cs`
  - `src/PetBox.Sessions/Services/SessionService.cs`
  - `src/PetBox.Sessions/Data/SessionStore.cs`, `SessionsDb.cs`, `SessionRow.cs`, `SessionContent.cs`, `Migrations/`
  - `src/PetBox.Sessions/Search/SessionTermIndex.cs`, `SessionFullScanIndex.cs`
  - `src/PetBox.Sessions/Episodic/DuckDbSessionEpisodicIndex.cs`
- **Key invariants:**
  - Storage is latest-snapshot last-write-wins (no SCD-2); `Version` is last message ordinal (content cursor, not optimistic concurrency).
  - Episodic index is transient per session, hydrated on demand and aged by idleness (no always-on global transcript index).
  - Full-scan requires per-call `fullScan:true` **and** system + project settings both enabled (independent non-cascading defaults-off switches).
  - Term/fullscan-only hits count as lexical confirmations (never floored as semantic-only noise) at the Web fusion layer.

## PetBox.LlmRouter (+ Contract)

- **Responsibility:** Project-scoped chat / embed / rerank facade over OpenAI-compatible upstreams, with a registry of endpoints/routes, capability routing, and circuit breaking. Consumers depend only on `PetBox.LlmRouter.Contract`; the implementation assembly is swappable via DI.
- **Key files:**
  - `src/PetBox.LlmRouter.Contract/ILlmClient.cs`, `ILlmRegistryAdmin.cs`, `LlmMessages.cs`, `LlmRegistry.cs`, `LlmCapability.cs`
  - `src/PetBox.LlmRouter/LlmRouterServiceCollectionExtensions.cs`
  - `src/PetBox.LlmRouter/Registry/LlmRegistryStore.cs`, `LlmRegistryValidator.cs`
  - `src/PetBox.LlmRouter/Routing/CapabilityRouter.cs`, `EndpointBreaker.cs`
  - `src/PetBox.LlmRouter/Http/OpenAiCompatibleClient.cs`, `CertPinningHttpClientProvider.cs`
- **Key invariants:**
  - DI registration is unconditional; `Feature.LlmRouter` gates public MCP/REST surface, not service existence (internal consumers always resolve `ILlmClient`).
  - Endpoint API keys are write-only secrets (accepted on set, never returned on get; resolved at call time).
  - Invoke scopes (`llm:invoke`) vs admin scopes (`llm:admin`) are separate; adapters depend on Contract only (NetArch-enforced).
  - OpenAI-compat chat path forbids cross-project `*` keys (project comes only from the key claim, not the URL).

## PetBox.Dashboard

- **Responsibility:** Pull-mode health polling BackgroundService: GETs configured `HealthEndpoint` URLs and appends `HealthReport` rows. Push-mode health needs no endpoint row (POST goes to Web Health API). CI polling is not registered as shipped.
- **Key files:**
  - `src/PetBox.Dashboard/HealthPoller.cs`
  - Related Core models: `src/PetBox.Core/Models/HealthEndpoint.cs`, `HealthReport.cs`, `HealthTags.cs`
  - Push REST: `src/PetBox.Web/Health/HealthApi.cs`
- **Key invariants:**
  - Registered only when `Features:Dashboard` is true.
  - Health reports are append-only, keyed by `(Svc, canonical Tags)`; UI shows latest per key.
  - On pull, endpoint ownership forces `tags["project"] = endpoint.ProjectKey` (payload cannot override).
  - `HealthEndpoint` rows are pull-mode config only; push sources POST `/api/health` directly.

## PetBox.Deploy

- **Responsibility:** Fleet-wide deployment control plane: node registry, desired container state per (service, node), poll/heartbeat reconcile, RunSpec allowlist, and failover sweeping. One node hosts containers from many projects, so storage is not per-project.
- **Key files:**
  - `src/PetBox.Deploy/Contract/IDeployService.cs`, `DeployContract.cs`, `RunSpec.cs`
  - `src/PetBox.Deploy/Services/DeployService.cs`, `DeployFailoverSweeper.cs`
  - `src/PetBox.Deploy/Data/DeployDb.cs`, `Deployment.cs`, `Node.cs`, `DeploySchema.cs`, `Migrations/`
  - Web REST thin surface: `src/PetBox.Web/Deploy/DeployApi.cs`
- **Key invariants:**
  - `IDeployService` is the single entry (NetArch forbids Web → `DeployDb`).
  - Single fleet-wide mutable DB (not project-scoped).
  - One deployment per `(Service, NodeId)`; `ConfigHash` computed server-side.
  - Poll response is the full desired set (agent self-fences absent services); heartbeat is a full snapshot (absent services → Missing).
  - `RunSpec` is a structural compose-subset allowlist (no privileged/cap-add/extra-args escape).
  - REST maps only `/agent/poll`, `/agent/heartbeat`, `/api/deploy/nodes`; richer CRUD is MCP/UI on `IDeployService`.

## PetBox.Web (entry + MCP + Razor)

- **Responsibility:** Single process entry point: DI composition, feature-gated endpoint mapping, Razor Pages SSR UI (htmx + Alpine + Tailwind/daisyUI), REST adapters for modules, MCP HTTP tool surface, ingestion (OTLP/Seq) adapters, cross-cutting search jobs (session digests / session search fusion), and frontend assets (TS via bun; Release-only bundle).
- **Key files:**
  - `src/PetBox.Web/Program.cs` — host composition, feature gates, middleware
  - `src/PetBox.Web/Routes.cs`
  - `src/PetBox.Web/Mcp/` — `ModuleMcp.cs`, `*Tools.cs`, filters (`McpToolScopeFilter`, `McpErrorEnvelopeFilter`, …)
  - `src/PetBox.Web/Pages/` — Razor UI
  - `src/PetBox.Web/Ingestion/` — OTLP (and related) ingest adapters
  - `src/PetBox.Web/Search/` — session digest job + session search fusion
  - `src/PetBox.Web/ts/` — client TypeScript (no inline JS in Razor)
  - Module API maps: `Health/`, `LlmRouter/`, `Deploy/`, `Memory/`, `Sessions/`, `Settings/`, `AgentDefs/`
- **Key invariants:**
  - Feature flags gate endpoint maps and BackgroundServices so disabled modules expose no public surface / zero runtime cost for those hosts.
  - MCP is HTTP, `Stateless=true`, at `/mcp` with `X-Api-Key`; tools/list filtering is **not** the auth boundary — call-time `AssertScope` / `AssertProject` is.
  - Success tool results with `outputSchema` must ship conforming `structuredContent`; errors use `isError` without fake structured success.
  - Agent-definition REST is always-on (Core DB; no feature flag).
  - UI chrome is English-only; E2E selectors use `data-testid`; no HTML in `.cs`, no inline JS in Razor.
  - Sessions feature surface is registered under the Tasks feature gate.

## Client SDKs (clients-net / clients-ts / clients-py)

- **Responsibility:** Thin consumer libraries for external apps: config resolve/provider + last-known-good cache, and Data SQL/schema helpers. Not part of the server module graph; published independently (NuGet / npm / PyPI).
- **Key files:**
  - **.NET:** `src/clients-net/PetBox.Client/` (`PetBoxClient`, `PetBoxDataClient`, transport), `PetBox.Client.Config/` (`PetBoxConfigProvider`, `LkgCache`), `PetBox.Client.Data.Linq2Db/`
  - **TypeScript:** `src/clients-ts/petbox-client/src/` (`client.ts`, `config.ts`, `data.ts`, `cache.ts`); wire package `src/clients-ts/petbox-wire/`
  - **Python:** `src/clients-py/petbox-client/src/petbox_client/` (`client.py`, `config.py`, `data.py`, `types.py`)
- **Key invariants:**
  - Clients are adapters over HTTP APIs (auth via API key); they do not embed server domain rules.
  - Config clients typically support LKG/cache so a temporary PetBox outage does not brick the consumer process.
  - Language SDKs stay thin and typed; server remains source of truth for authorization and resolve semantics.
