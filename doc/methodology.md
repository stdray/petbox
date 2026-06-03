# PetBox project methodology

> How we run PetBox itself — the model agents and the maintainer follow so the
> project's state stays **surveyable** and **deliberation is durable** (thinking
> isn't lost when a session closes). Designed 2026-06-01 (see decision-log).

## Why

State used to be smeared across 5 places (plan.md / roadmap board / parking /
sessions / decision-log), hand-synced, with a drifting style. Two pains:
- **Thinking gets lost.** Discuss-but-don't-execute (reject an idea, defer with
  unease, just explore) → the reasoning dies with the session and we circle back.
  The asset is the **reasoning, not the verdict**.
- **`Done` ≠ approved.** Agents marked work Done and shipped; the maintainer had no
  gate and repeatedly reopened. Agents must not self-certify completion.

## Two interacting but separate processes

### P1 — requirements evolution (functional + non-functional). Artifact: the spec tree.
Unit of work = an **Idea** (initially a tangled set of requirements). Lifecycle:
`raw idea → refined statement → plan to update the spec → new spec version`.
A **rejected** idea never enters the tree — it stays a record "considered, rejected: why".

### P2 — technical. Artifact: tasks.
Bridge from P1: `spec leaf with no linked tasks → create tasks → backlog`.

## Entities

1. **Session** — raw transcript of one episode; source material.
2. **Idea / Deliberation** — a thread of thinking, distilled from sessions, with an
   outcome `{open, exploring, rejected, deferred, accepted}` + reasoning. A topic →
   0..N tasks. **Separate from the plan.**
3. **Spec / Feature** — a temporal tree. **Invariant: only DEFINED requirements live
   in the tree.** Undefined/in-flux = an Idea, not a tree node; changing a requirement
   = a new Idea (same lifecycle) → a new spec version. Branches are functional AND
   non-functional (`perf/`, `security/`…) with requirement leaves; features/tasks
   cross-reference NFR leaves via M:N links.
4. **Task** — technical unit; lives in a backlog; carries `type`
   (`feature|bug|chore|…`, `auto` = the agent classifies); M:N-linked to spec leaves;
   provenance (originating issue + spec item); `commitRef`.
5. **Intake** — two queues (agent issues, user issues) → triage → {reject-with-reason
   | task into backlog with links | (user) idea → P1}.

## Task lifecycle + the approve gate

`backlog → (selected into an iteration) → InProgress → Review (agent finished) →
tests → APPROVE (maintainer) → Done`; reject → back with a reason.

**An agent never sets the final `Done` itself — its ceiling is `Review`.** The
maintainer confirms.

## Iterations

An iteration = a **filtered backlog** (pull a batch by: no blockers, priority,
connectivity, simplicity, clear statement; plus carry-overs that failed
review/tests/approval). No sprint ceremony / story points. It closes with a
**release** whose artifacts are: test results + version tag + build + deploy target
(= our CI `ci.NNN` + `commitRef` + deploy).

## Spec-node status — computed bottom-up, type-aware

A spec leaf's status is derived from its linked tasks (not set by hand):
- no implementing (`feature`/`chore`) tasks, or not all Done → **not started / in progress**;
- all implementing tasks Done, no open `bug` tasks → **Done**;
- all implementing tasks Done, but open `bug` tasks → **Done with defects** (a bug is
  not the same as "not built");
- parents aggregate their children. A leaf with no tasks = **not started**.

**Surveyability:** the feature tree with computed status is the primary view. Kanban /
Gantt are projections over it (Gantt is a poor fit here — no estimates, constant churn).

## Writing requirements in the spec tree

A spec node is a **requirement at the owner/value altitude** — a promise that would
survive a reimplementation. The *mechanism* (data shape, validation rules, API verbs,
scopes, storage) is NOT a requirement; it lives in the **work task** (and the code).

- **Altitude test:** *"Would this change if we reimplemented the mechanism without
  changing the promise to the user?"* Yes → it's design → work task. No, it survives →
  it's a requirement → spec. (E.g. "discussion can be held under a node" is a
  requirement; "threaded vs flat, keyed by NodeId, optimistic edit, delete-leaf-only"
  is design → work task.)
- **Format — terse-normative "shall" (EARS without the boilerplate subject):** the
  node **title** is the capability; the **body** is one normative line carrying the
  obligation + any condition/consequence, or empty when the title already says it.
  Obligation keywords: ДОЛЖЕН / СЛЕДУЕТ / МОЖЕТ (RFC 2119 MUST/SHOULD/MAY). Functional
  requirement → `area:*` tag; non-functional / invariant → `concern:*`.
- **Granularity — atomic but few.** One requirement per node, but at the owner altitude
  there are only a handful. Do NOT pre-atomize implementation detail into nodes. Add a
  child node only when a part earns its own lifecycle / delivery.
- **Link a task to each requirement it delivers (M:N), not just the umbrella.**
  `task_spec` / `specRef` may point at ANY spec node; `delivery` aggregates a node's tasks
  over its whole `part_of` subtree, so leaf links automatically roll up to the block and
  root. Linking only at the umbrella node leaves the leaves at `not_started` and hides
  per-requirement progress — so link the leaves the task actually delivers (at the owner
  altitude they are few: this is honest M:N, not link explosion). A requirement only
  partially delivered stays `in_progress` because one of its linked tasks isn't Done yet
  (e.g. a read-only UI delivered now + an interactive-UI task still Pending). Note the two
  signals: `status` (draft/defined) = is the requirement agreed; `delivery` (computed) =
  is it built.
- **Process:** before editing the spec, write the **plan to update it** as the
  deliberation artifact — an `artifact:spec_plan`-tagged comment on the originating idea
  — then apply the change.

## It rides on what PetBox already has
- Spec = temporal tree ← `TemporalStore` (SCD-2).
- Iteration = release ← CI `ci.NNN` + `commitRef` + deploy.
- Intake ← the `incoming` phase + `report.issue`.
- `type=auto` ← the agent already classifies incoming requests reliably.

## Adoption status

This is the target model. It is adopted incrementally; until a feature is built,
the corresponding convention applies on the current Tasks primitives (e.g. separate
`spec`/`backlog` boards, the approve gate by convention). See the roadmap in the
`$system/roadmap` board (`mcp-typing` and methodology phases) and `doc/decision-log.md`.
