# Spec backfill proposal (draft)

> **Proposal. Not accepted, not on the board.**  
> Requires Claude verification (quote grep + two-sided altitude test) + owner accept.  
> Language: English MUST/SHOULD (harvest convention for this trial). Live board may keep Russian titles.

**Source map:** `doc/proposals/spec-backfill-map.jsonl` (354 rows)  
**Intake:** `spec-backfill-from-code`  
**Complementary idea (do not replace):** `spec-full-review` (existing nodes + floating invariants)  
**Hang under (name only — do not create):** new idea e.g. `spec-backfill-from-code` (promote intake → idea) or umbrella idea `spec-catalog-completeness`

---

## Harvest summary (for context)

| Metric | Count |
|--------|------:|
| Map rows (all buckets) | 354 |
| `promise` | 174 |
| `invariant` | 69 |
| `architecture` | 44 |
| `policy` | 34 |
| `mechanism-detail` | 33 |
| `promise` ∩ `likely-new` | 117 |
| `promise` ∩ `uncertain` | 3 |

This proposal does **not** dump all 117 rows as nodes. It elevates only groups that pass the dual altitude test (survives reimplementation **and** is normative/checkable). Rows that are still implementation-shaped stay in the map as `mechanism-detail` / leave-for-Claude.

**Out of scope here:** fate of `invariant` / `policy` (→ floating-invariants problem on `spec-full-review`).

---

## Proposed umbrellas → leaves

### 1. `feature-module-gating` — Feature-flagged modules

- **title:** Feature-flagged module registration  
- **body:** PetBox MUST register a module’s services, endpoints, and background work only when that module’s feature flag is enabled; a disabled module MUST impose zero runtime surface.  
- **tags:** `concern:architecture`  
- **provenance:** map claims from `ModuleExtensions.cs`, Program feature registration, `_partial-core` / `_partial-llm-deploy-mcp-clients`  
- **leaves:**
  - `feature-zero-cost-off` — A disabled subsystem MUST NOT expose HTTP/MCP routes or host its BackgroundServices.  
  - **provenance:** `src/PetBox.Core/Features/ModuleExtensions.cs:8-15`

**Altitude note:** Strong product invariant; already implied by AGENTS hard invariants but **no** live `spec` key found in catalog.

---

### 2. `auth-api-key-surface` — API key authentication surface

- **title:** API key authentication surface  
- **body:** PetBox MUST authenticate machine callers via API keys with an explicit project claim and an enumerated set of scopes.  
- **tags:** `area:access`  
- **relation:** parent near existing `access-control` (do not duplicate scope enum / cross-project `*` — already `access-cross-project-key`, `access-root-explicit`)  
- **leaves (gaps vs catalog):**
  - `auth-mode-local-remote` — Auth MUST support `local` (validate against this instance’s catalog) and `remote` (delegate to a central `/api/auth/validate`).  
    - **provenance:** `AuthConfiguration.cs`, `RemoteAuthHandler.cs`, `AuthApi.cs`
  - `auth-header-accept` — Callers MUST be able to present keys via `X-Api-Key` (preferred), legacy `X-YobaConf-ApiKey`, or `Authorization: Bearer|Token`.  
    - **provenance:** `ApiKeyAuthenticationHandler.cs:20-29`
  - `auth-key-expiry` — A key with `ExpiresAt` set MUST be rejected after that instant; null MUST mean non-expiring.  
    - **provenance:** `ApiKeyAuthenticationHandler.cs:39-41`
  - `auth-config-sourced-keys` — Config-declared API keys MUST override/coexist with DB keys and MUST NOT be revocable through the UI key lifecycle.  
    - **provenance:** `ConfigApiKeyLookup.cs:19-22`
  - `auth-validate-endpoint` — The instance MUST expose `GET /api/auth/validate` returning project claim + scopes for remote-mode peers.  
    - **provenance:** `AuthApi.cs:10-19`

**Covered already (do not re-add):** exact scope checks, `admin:provision` root, wildcard **project** claim, root-explicit.

---

### 3. `workspace-membership` — Human workspace tenancy

- **title:** Workspace membership roles  
- **body:** PetBox MUST model human access to a workspace with roles Admin, Member, and Viewer, and MUST gate workspace administration to sysadmin or workspace Admin.  
- **tags:** `area:access`  
- **leaves:**
  - `workspace-roles` — Membership roles MUST be Admin | Member | Viewer.  
    - **provenance:** `WorkspaceMember.cs:3-11`
  - `workspace-admin-gate` — Workspace administration MUST require sysadmin or Admin role on that workspace.  
    - **provenance:** `WorkspaceRoleClaims.cs:13-19`
  - `admin-bootstrap-once` — Env-configured admin bootstrap MUST run only while no `$system` administrator exists.  
    - **provenance:** `AdminBootstrapper.cs:20-28`
  - `reserved-workspace-project` — Product MUST provide reserved `$workspace` under `$system` as the cross-project shared-memory container.  
    - **provenance:** `M028_SeedWorkspaceMemory.cs:8-16`  
    - **note:** Partially remembered in canon; no dedicated spec key found.
  - `project-delete-cascade-boundary` — Project delete MUST cascade owned Core bookkeeping rows listed in code and MUST NOT cascade tag-based ConfigBindings / HealthReports.  
    - **provenance:** `ProjectDeletion.cs:7-20`  
    - **altitude:** borderline (cascade list is mechanism-shaped) — Claude may demote to invariant.

---

### 4. `config-resolve` — Tag-based configuration resolve

- **title:** Tag-based configuration resolve  
- **body:** PetBox MUST resolve configuration bindings by pure tag matching such that the most specific matching tag set wins, deterministically for a given tag vector.  
- **tags:** `area:config`  
- **leaves:**
  - `config-subset-match` — A binding is a candidate only when every binding tag is present in the request tags (subset, not equality).  
  - `config-most-specific-wins` — Among candidates for a path, the largest matching tag count MUST win.  
  - `config-ambiguous-409` — Equal-specificity collisions MUST fail loud (HTTP 409) with path + candidate ids.  
  - `config-path-exact-ci` — Path match MUST be case-insensitive exact (no prefix/glob).  
  - `config-tags-ci` — Tag comparison MUST be case-insensitive.  
  - `config-soft-delete-excluded` — Soft-deleted bindings MUST be excluded from resolve.  
  - `config-bulk-v1-conf` — Canonical bulk resolve MUST be `GET /v1/conf` requiring `config:read`, auto-injecting `ws:` / `project:` from the key.  
  - `config-etag-304` — Resolved set MUST support ETag / If-None-Match → 304.  
  - `config-secret-decrypt` — Secret bindings MUST decrypt at resolve when master key material is available.  
  - **provenance:** `ResolvePipeline.cs`, `ConfigApi.cs`, `SecretEncryptor.cs` (many map rows)

**Existing nearby:** `config-binding-create-ui`, `settings-uniform-override`, `env-from-config`, client offline — **none** encode the resolve algorithm itself. This is the largest clear catalog gap.

---

### 5. `data-sql-module` — Per-project data databases

- **title:** Per-project SQL data plane  
- **body:** PetBox MUST provide per-project named SQLite databases with a raw-SQL pass-through and hash-idempotent schema apply, gated by `data:read` / `data:write` / `data:schema`.  
- **tags:** `area:data` (new area tag if catalog allows)  
- **leaves:**
  - `data-db-lifecycle` — Create/list/delete named DataDbs per project.  
  - `data-raw-sql` — Query (read) and exec (write) raw SQL with timeout defaults/caps.  
  - `data-schema-hash-idempotent` — Same name+hash → AlreadyApplied; same name different hash → 409 with both hashes.  
  - `data-missing-db-404` — Missing DB → not-found.  
  - `data-sqlite-full-507` — SQLITE_FULL → HTTP 507.  
  - `data-orphan-cleanup` — Orphan on-disk files without metadata MUST be cleaned eventually.  
  - **provenance:** `DataDbsApi.cs`, `QueryExecApi.cs`, `SchemaRunner.cs`, `OrphanCleanupService.cs`

**Note:** Entire Data module capability set appears **absent** from the live spec key catalog — high-value backfill.

---

### 6. `log-named-lifecycle` — Named log lifecycle & ingest gates

- **title:** Named log lifecycle  
- **body:** PetBox MUST require an explicit named log before ingest and MUST NOT auto-vivify logs on the public ingest path (except the reserved self-log).  
- **tags:** `area:logs`  
- **leaves:**
  - `log-create-before-ingest` — Path-based CLEF/OTLP/Seq-compat MUST 404 with create-first when the log is missing.  
  - `log-name-spec` — Log names MUST match the shared name regex used by DataDbs.  
  - `log-eager-materialize` — Create MUST materialize SQLite schema eagerly.  
  - `log-partial-batch-ingest` — CLEF batches MUST accept partial success; all-failed → 400.  
  - `log-live-tail` — Live-tail MUST stream new events as SSE with bounded DropOldest buffer.  
  - `log-retention-cascade` — Retention MUST cascade project → workspace → system with documented defaults.  
  - `log-share-column-mask` — Share export MUST support Keep/Mask/Hide column modes.  
  - **provenance:** `LogApi.cs`, `LogStore.cs`, `RetentionService.cs`, `InMemoryTailBroadcaster.cs`, `TsvExporter.cs`

**Covered nearby:** `compat-ingest`, `compat-seq`, many `kql-*`, `logs-share-opaque`, `log-query-*` — lifecycle gates themselves look **under-specced**.

**Controversial / Claude check:**
- Events-only retention vs spans/metrics tables (map: residual risk) — may be gap or intentional invariant.
- CiPoller referenced in Dashboard comments but **not shipped** — do **not** propose as promise.

---

### 7. `log-property-keys` — Flat dotted property addressing

- **title:** Flat dotted log property keys  
- **body:** PetBox MUST expose log event properties as flat dotted keys addressable in query/search with stable normalization at write and search boundaries.  
- **tags:** `area:logs`  
- **leaves:**
  - `log-properties-dotted` — Dots in keys MUST be preserved; only unsafe chars normalized.  
  - `log-properties-collision` — Collision handling MUST be deterministic (`_2`/`_3` suffix pattern per harvest).  
  - **provenance:** `KqlPropertyKeys.cs`  
- **relation:** May partially overlap `kql-typed-properties` / Properties flat-key work (ci.558) — Claude must dedup against those node bodies, not only keys.

---

### 8. `temporal-write-semantics` — Temporal concurrency contract

- **title:** Temporal write concurrency  
- **body:** PetBox MUST expose explicit concurrency outcomes on temporal stores (Stale with ChangedFields, FutureBaseline, AutoResolved bookkeeping) rather than silent last-write-wins.  
- **tags:** `concern:correctness`  
- **leaves:**
  - `temporal-stale-changed-fields`  
  - `temporal-future-baseline`  
  - `temporal-auto-resolved`  
  - **provenance:** `TemporalStore.cs`  
- **relation:** Overlaps spirit of `explicit-write-semantics` / `baseline-watermark` / `sinceversion-contract` — verify before adding; may be leaf under those umbrellas rather than new root.

---

### 9. `mcp-agent-contract` — MCP wire contract for agents

- **title:** MCP agent wire contract  
- **body:** PetBox MUST expose a scope-gated MCP tool surface with stable underscore tool names, a uniform body length knob, and a structured error envelope suitable for strict clients.  
- **tags:** `concern:dx`, `concern:architecture`  
- **leaves:**
  - `mcp-underscore-names` — Tool wire names MUST be `module_verb` underscores.  
  - `mcp-error-envelope` — Tool failures MUST surface `{error:{type,message,detail}}` with `IsError=true` and no `structuredContent`.  
  - `mcp-bodylen` — Body-carrying tools MUST honor uniform `bodyLen` (`-1` / `0` / `N` / default).  
  - `mcp-whoami` — `whoami` MUST work for any authenticated key without extra scope.  
  - `mcp-report-issue` — `report_issue` MUST file into fixed `$system`/`client-issues` (not caller project).  
  - `mcp-http-apikey` — `/mcp` MUST use the same ApiKey auth as REST.  
  - `mcp-tools-list-scope-trim` — `tools/list` MAY trim by module scopes as UX; harvest notes fail-open — **not** a security boundary (pair with authz on invoke).  
  - **provenance:** `McpErrorEnvelopeFilter.cs`, `WhoAmITools.cs`, `ReportTools.cs`, `ModuleMcp.cs`, `Program.cs`

**Covered nearby:** `typed-mcp-inputs/outputs`, `tool-description-economy`, `uniform-entity-verbs`, `surface-economy` — still no dedicated whoami/report_issue/error-envelope keys.

---

### 10. `tasks-engine-effects` — Tasks/methodology runtime effects

- **title:** Tasks runtime effects beyond presets  
- **body:** PetBox MUST enforce board write guards, delivery rollup, relation effects, and reason persistence as product behavior of the methodology engine.  
- **tags:** `area:tasks`, `area:methodology`  
- **leaves (gaps — many presets already keyed):**
  - `delivery-rollup-semantics` — Delivery MUST roll up `part_of` + inbound `task_spec` into `not_started|in_progress|done|done_with_defects`.  
  - `reason-artifact-persist` — Accepted `RequiresReason` reasons MUST persist as `artifact:reason` comments.  
  - `supersedes-system-cancel` — `supersedes` MUST soft-link and system-move superseded node to terminal-cancel without approve gate.  
  - `blocks-last-blocker-release` — Unblock MUST release only when last blocker clears.  
  - `board-close-write-reject` — Closed boards MUST reject writes and stay readable.  
  - `board-auto-vivify` — First write MAY auto-create a missing board (documented ergonomics exception).  
  - `priority-sparse-lower-first` — Priority MUST be sparse int, lower-first; anti-starvation is **process**, not product (map marks NOT productized).  
  - `chore-specless` — Work type `chore` MUST mean spec-less hygiene (no `specRef`).  
  - **provenance:** TasksService, TaskTransitionEffects, MethodologyDefinition, MethodologyPresets, MCP TasksTools

**Caution:** Large overlap with methodology primitives already on the board — Claude should attach leaves only where bodies truly missing.

---

### 11. `memory-write-taxonomy` — Memory write taxonomy & PATCH

- **title:** Memory entry taxonomy and write semantics  
- **body:** PetBox MUST require a closed Type taxonomy on create, apply PATCH semantics on edit, and soft-delete temporal history.  
- **tags:** `area:memory`  
- **leaves:**
  - `memory-type-closed-set` — Type MUST be User|Feedback|Project|Reference; invalid → explicit error.  
  - `memory-type-required-on-create` — New entries (version 0) MUST supply Type; edits MAY omit.  
  - `memory-tags-patch` — Tags: null keep, `[]` clear, list replace; normalized.  
  - `memory-soft-delete-temporal` — Delete closes active revision; history kept.  
  - `memory-write-ack-scoped` — Write ack echoes only this call’s keys; empty when not applied.  
  - `memory-list-default-order` — Listing without `q` MUST default Updated desc; reject sort-by-relevance without query.  
  - **provenance:** `MemoryService.cs`, `MemoryContract.cs`

**Covered nearby:** autocapture cluster, search hybrid, usage observability, canon wiring.

---

### 12. `session-identity-meta` — Session identity & metadata

- **title:** Session identity and observed metadata  
- **body:** PetBox MUST resolve session ids with exact-over-prefix rules and MUST store observed client metadata as a JSON object with keep-on-null semantics.  
- **tags:** `area:sessions`  
- **leaves:**
  - `session-id-resolve` — Exact match wins; ambiguous prefix → Ambiguous; writes exact-only.  
  - `session-metajson-lww` — MetaJson MUST be object; null/whitespace keeps existing.  
  - **provenance:** `ISessionService.cs`, `SessionRow.cs`

---

### 13. `health-pull-model` — Health pull reporting

- **title:** Health endpoint pull polling  
- **body:** PetBox MUST periodically pull configured health endpoints and append reports with a documented JSON shape and `Source=pull`.  
- **tags:** `area:health`  
- **leaves:**
  - `health-poller-pull` — HealthPoller MUST GET enabled endpoints and append HealthReport.  
  - `health-push-project-tag` — Push path MUST require `tags.project` and project-scoped authorization.  
  - **provenance:** `HealthPoller.cs`, `HealthApi.cs`  
- **relation:** Complements `health-read-surface`.

---

### 14. `llm-openai-compat` — OpenAI-compatible chat entry

- **title:** OpenAI-compatible chat entrypoint  
- **body:** Non-agent consumers MUST be able to call chat via `POST /v1/chat/completions` with `llm:invoke`; streaming is not required yet.  
- **tags:** `area:llm-router`  
- **provenance:** `LlmRouterApi.cs:12`  
- **relation:** Near `llm-public-surface` / `llm-capabilities` — Claude dedup against those bodies.

**Also map:** null-tier routes serve any tier (`CapabilityRouter.cs`) — leaf under `llmrouter` if not already stated.

---

### 15. `client-zero-deps` (optional leaf)

- **title:** Python client zero runtime dependencies  
- **body:** The Python petbox-client MUST ship with zero runtime dependencies (stdlib only), consistent with the TS config/data client stance.  
- **tags:** `area:clients`  
- **provenance:** `src/clients-py/petbox-client/pyproject.toml:18`  
- **relation:** Under `client-sdk` / `sdk-release-uniform`.

---

## Suggested node counts (after Claude filter)

| Tier | Proposed now | Expected after altitude filter |
|------|-------------:|-------------------------------:|
| New umbrellas | 13–15 | 8–12 |
| New leaves | ~55 | 25–40 |
| Attach-as-leaf under existing umbrellas | ~15 | most of temporal/mcp/llm |

---

## Possible deprecations / contradictions (for Claude, not auto-applied)

1. **CiPoller** — docs/comments suggest CI polling; Dashboard only ships `HealthPoller`. Do not claim CI polling as capability.  
2. **Anti-starvation priority** — process/memory claim; code is sparse lower-first only. Do not promote process as product promise.  
3. **Agent ceiling Review** — methodology presets use `RequiresApproval` without always `EnforceApproval`; dogfood policy ≠ enforced product. Map marks policy.  
4. **tools/list scope-trim fail-open** — if marketed as security, contradict code; either promise “UX only” or fix product.  
5. **Retention events-only** — spans/metrics may not be swept; document or fix.  
6. **Doc vs code delivery RequiredTypes** — methodology doc wording vs `RequiredTypes=["feature"]` — code is authority for product promise.

---

## What NOT to put on the board from this pass

- All `invariant` / `policy` / `architecture` / `mechanism-detail` rows (stay in map).  
- Re-statements of existing keys (`kql-*`, `autocapture-*`, deploy fleet, wiring matrix, …) marked `covered-by:*` in the map.  
- AGENTS process contract lines (worktree, agent ceiling, commit convention) — **policy**, not product catalog.

---

## Recommended next process steps (owner / Claude)

1. Claude: grep every proposed leaf’s `quote` from the map; drop failures.  
2. Claude: two-sided altitude filter; demote leftovers to invariant/policy notes on `spec-full-review`.  
3. Promote intake → idea (or new idea) → `artifact:spec_plan` from a **verified** subset of this file.  
4. Owner accept → write `spec` nodes with `ideaRef`.  
5. Do **not** treat this harvest branch as shipping product code — artifacts only.

---

## Idea name suggestion (do not create)

- `spec-backfill-from-code` (promote the intake) — keeps provenance  
- or `spec-catalog-completeness` — if owner wants a durable idea separate from this trial harvest
