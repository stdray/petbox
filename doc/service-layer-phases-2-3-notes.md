# Service-layer refactor — Phase 1g done; Phase 2 & 3 readiness notes

Branch `feat/service-layer`. Phase 1 + 1g are complete and committed (5 commits,
build + 473 unit tests green, **not merged/deployed**). Each module now has a single
door enforced by a NetArchTest:

| Module   | Door                     | Notes |
|----------|--------------------------|-------|
| Tasks    | `ITasksService`          | all domain logic out of `TasksTools`; UI + `ReportTools` thin |
| Memory   | `IMemoryService`         | taxonomy/tags/FTS/temporal; 3 pages + `MemoryTools` thin |
| Sessions | `ISessionService`        | MCP `session.append` + REST Stop-hook unified |
| Data     | `IDataSqlService`        | converged drifted raw-SQL query/exec (MCP vs REST) |
| Log      | `ILogQueryService`       | converged KQL orchestration (MCP vs REST) around shared `KqlTransformer` |

**Enforcement = NetArchTest, not the `internal` clamp** (tests need white-box
`GetContext`; a public interface can't expose an internal return type). Sanctioned
generic store consumers left as-is (single consumer / no UI divergence): `RelationTools`,
`EntityTools` (catalog CRUD), `entity.describe`, the Data/Log browse pages, OTLP ingestion.

---

## Phase 2 — declarative validation (FluentValidation): OPEN DECISIONS

The plan wants imperative checks replaced by declarative validators using an old-vs-new
(`EntityChange<T>`) pattern. After Phase 1 the Tasks rules already live in one place
(`TasksService`), which changes the calculus:

1. **Status/transition** is already a single declarative point (`WorkflowEngine.Validate`).
   Wrapping it in `AbstractValidator<…>` adds ceremony, not isolation — re-expressing the
   transition table in FluentValidation would duplicate or double-call it. **Recommendation:
   leave `WorkflowEngine` as the validator; don't wrap.**
2. **Async cross-entity rules** (`ValidateSpecRefsAsync` — specRef resolves to a spec board
   via the project node index; `RequireBlockersAsync` — Blocked needs a live `blocks` edge)
   need the store/relations. FluentValidation `MustAsync` *can* hold those, but the validator
   then carries DB access — i.e. it becomes a second service. **Recommendation: keep these in
   the service; they are not a FluentValidation fit.**
3. **NEW invariants** the plan implies — immutable `NodeId`, immutable `type` after create —
   are **not enforced today**. This is the real, valuable part of Phase 2, and it is exactly
   where `EntityChange<PlanNode>` (Old vs New) shines. **But adding an invariant changes prod
   behavior** (e.g. a future edit that flips a node's type would start failing). NEEDS A
   DECISION: do we want type/NodeId immutable-after-create? (Likely yes — but confirm.)

**Proposed Phase 2 scope once confirmed:** add `internal sealed record EntityChange<T>(T? Old,
T New)` in `PetBox.Tasks/Validation/`, plus `PlanNodeChangeValidator : AbstractValidator
<EntityChange<PlanNode>>` covering the immutable-field rules (the genuinely new, declarative,
context-carrying part), invoked from `TasksService.UpsertAsync` per node. Keep
`WorkflowEngine` and the async cross-entity rules where they are. Tests are the spec — any
re-expression must keep the suite green and preserve error messages.

---

## Phase 3 — single physical DB with per-module bounded context: OPEN DECISION + MIGRATION

Plan target: one physical SQLite DB, each module mapped to only its own tables (a bounded
context) so it can be split back out later. Two unresolved points:

1. **Per-scope file fate (the flagged sub-decision):** today Tasks/Memory/Log/Data are
   *sharded per scope* (`tasks/<proj>/<board>.db`, one file per board/store/log/userdb) via
   `IScopedDbFactory<T>`. "One physical DB" forces a choice:
   - (a) **Collapse** every scope's rows into shared tables keyed by (project, scope) — true
     single DB, but loses per-file isolation/quota and is a large temporal-data migration.
   - (b) **Keep sharding**, and only merge the *catalog* (already in `petbox.db`) — i.e. Phase 3
     becomes "single catalog DB + bounded contexts over the existing shards." Much lower risk.
   - The user-facing Data tier explicitly stays per-file (quota/isolation per user DB), so (b)
     is likely right at least there. **NEEDS A DECISION before any code.**
2. **Migration is the stop line.** The plan's zero-loss procedure (SSH to prod, `.backup`/
   `VACUUM INTO` snapshot, off-box `tar`+`scp`, FluentMigrator-based migrator preserving
   temporal columns + `Relation`, row-count reconciliation, cutover keeping originals) is the
   *physical transfer of data* — explicitly out of scope for the autonomous run.

**Phase 3 is blocked on decision (1).** No code should land until the file-fate question is
answered, because it dictates the entire context/mapping shape.

---

## Recommended next steps (need user input)

- Confirm the Phase 2 immutable-field invariants (type/NodeId after create) → then implement
  the `EntityChange<PlanNode>` validator slice on Tasks as the exemplar.
- Decide Phase 3 file-fate (collapse vs keep-shard + single catalog) → then design the context
  mapping; run the prod migration as a separate, supervised step.
- Merge/deploy `feat/service-layer` (Phase 1 + 1g) when ready; per deploy ritual, run a live
  smoke against prod MCP/REST after (see `feedback_post_deploy_live_smoke`).
