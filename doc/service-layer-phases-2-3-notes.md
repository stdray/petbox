# Service-layer refactor — Phase 1g done; Phase 2 & 3 readiness notes

Branch `feat/service-layer`. Phase 1 + 1g are complete and committed (5 commits,
build + 473 unit tests green, **not merged/deployed**). Each module now has a single
door enforced by a NetArchTest:

| Module   | Door                     | Notes |
|----------|--------------------------|-------|
| Tasks    | `ITasksService`          | all domain logic out of `TasksTools`; UI + `ReportTools` thin |
| Memory   | `IMemoryService`         | taxonomy/tags/FTS/temporal; 3 pages + `MemoryTools` thin |
| Sessions | `ISessionService`        | MCP `session.upsert` + REST Stop-hook unified |
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

## Phase 3 — single physical DB with per-module bounded context: CLOSED (won't do)

**Decision 2026-06-03 (user): Phase 3 is closed, not executed.** Rationale:

- Per-module *content* contexts already exist and are already bounded: `TasksDb` / `MemoryDb`
  / `SessionsDb` / `LogDb` are each a `DataConnection` over their own shard file and see only
  their own tables. Only the *catalog* god-context `PetBoxDb` mixes modules' rows — splitting
  it would be the same file/tables behind narrower views (cosmetic, and risky given its reach).
- So the "keep-shard + single catalog + bounded contexts" option (b) moves NO data and has
  almost no material code to write — it largely affirms what already exists.
- The "collapse all shards into shared tables" option (a) would be a true single DB but: (i)
  loses per-file isolation + quotas (the user-facing Data tier must stay per-file regardless),
  and (ii) requires a large temporal-data migration on prod.
- The only concrete wins of a physical merge are atomic cross-module writes (today a
  `tasks.upsert` writes the node to its shard and the `Relation` edge to `petbox.db` in two
  files, non-atomic) and one-file backup. For a single-user system the atomicity gap is
  low-stakes and idempotently self-heals on re-upsert; if it ever bites, fix it narrowly
  (move `Relations` into the board shard, or wrap the two writers in one transaction where it
  matters) rather than merging every DB.

Sharding is treated as a **feature** (isolation, per-file quota, `rm`-to-delete), not debt.
The service layer (Phases 1–2) already delivered the actual goal — one door per module, no
divergent DB paths. **The refactor is considered complete at Phase 2.**

---

## Done / remaining

- Phase 1 + 1g + Phase 2 slice: **done**, committed on `feat/service-layer`.
- Phase 3: **closed (won't do)** — see above.
- Remaining: merge/deploy `feat/service-layer` when ready; per deploy ritual run a live smoke
  against prod MCP/REST after (see `feedback_post_deploy_live_smoke`). Optional low-priority
  follow-ups: route the Data/Log browse pages + OTLP ingestion through their services; tighten
  the Tasks quick-add path.
