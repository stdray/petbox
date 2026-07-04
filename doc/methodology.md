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
   provenance (originating issue + spec item); `commits[]`.
5. **Intake** — the inbox (agent + user queues) → triage → {reject-with-reason |
   promote to a task (only if a spec node already exists) | escalate to an idea (no spec
   reflection → P1)}. See "Intake — the inbox, and the no-spec rule" below.

## Task lifecycle + the approve gate

`backlog → (selected into an iteration) → InProgress → Review (agent finished) →
tests → APPROVE (maintainer) → Done`; reject → back with a reason.

**An agent never sets the final `Done` itself — its ceiling is `Review`.** The
maintainer confirms.

**Hand over a link, not a slug.** Whenever the agent creates a node the maintainer should
see, or needs a decision from them on one (drive a task to `Review` / an idea to `Review`,
ask to approve / accept / set `Done`, or "look at X"), it surfaces the node's permalink
(`include_url:true` on `tasks_upsert`/`tasks_get`/`tasks_methodology_get`, then the returned
`url`) as a clickable link — the maintainer decides from the UI, and a direct link is the
shortest path to the thing they must act on.

## Idea lifecycle + the spec-approval gate

Symmetric to the task gate: an Idea (deliberation) flows
`raw → exploring → Review → accepted`; `Review → exploring` sends it back for more
thinking.

- The agent's ceiling is **`Review`**, not `accepted` — exactly as its ceiling on a task
  is `Review`, not `Done`. The agent works the idea, puts it in `Review`, and stops.
- **An idea may not enter `Review` without a `spec_plan` artifact** — an
  `artifact:spec_plan`-tagged comment on the idea stating the concrete spec changes. No
  plan, no review.
- The maintainer approves **`Review → accepted`** (= approves the spec-change set); only
  then is the spec updated. This is the spec-change equivalent of `Review → Done`.

Enforced in code: the ideas FSM has `review` (between exploring and accepted) and
`exploring → review` is rejected without an `artifact:spec_plan` comment (the guard lives
in `TasksService`, reading `ICommentService`; `WorkflowEngine` stays pure). The direct
`exploring → accepted` transition was removed — you must pass through `review`. The
`review → accepted` approval itself stays a convention (the agent shouldn't self-accept);
`enforceApproval` is off until there's a maintainer/agent role distinction.

## Intake — the inbox, and the "no spec yet" rule

Intake is the **inbox for raw observations** that are neither an idea nor a task yet:
bugs ("font too small"), questions ("does model X cope with the methodology?"), wishes.
Two queues (agent-reported, user-reported); items land at `reported` (via `report_issue`
or a direct upsert). It is NOT part of the requirements pipeline — it's a holding area
until each item is routed.

**Intake is deferred triage, NOT a mandatory gateway.** It exists for reports whose
reporter is not the router (external users, an agent without the context to diagnose) and
for observations you don't want to route right now (noticed mid-task — park it, don't
derail). When the reporter CAN route — the maintainer, or an agent with a clear diagnosis —
and the destination is obvious, create the node at the destination directly and skip
intake: engineering hygiene → a work `chore` (spec-less by design, no `specRef`); a bug
violating an EXISTING spec node → a work `bug` with `specRef`; a product thought with no
spec reflection → an idea. The chain's integrity is protected by the destination rules
themselves (feature/bug still requires `specRef`, a spec write still requires an accepted
idea) — an intake hop adds nothing when the routing is already known.

**Triage** moves an item to exactly one of:

- **reject** — `wontfix` / `duplicate`, with a reason (the record stays; thinking isn't lost).
- **promote to a task** — *only when the item maps to an EXISTING spec node.* The work task
  links that spec node (`specRef` → `task_spec`) and the intake issue (`issue_task`); the
  issue auto-closes when the task reaches `Done`. Spec-less engineering hygiene promotes to
  a `chore` (no spec node needed).
- **escalate to an idea** — *when the item has NO reflection in the spec.* You can't make a
  work task for it: a work feature/bug REQUIRES a `specRef`, and there is no spec node to
  point at. That absence IS the signal — the requirement was never specified. The item
  becomes the **seed of an Idea**, which runs the gate (`idea → review[+spec_plan] → accept
  → spec`) and only then spawns work. (E.g. "font too small" with no UI spec → an idea
  "tasks are viewable in the UI" → spec → a `bug` task linked to that new spec node.)

So the chain holds end to end: **every work task traces to a spec node, every spec node to
an accepted idea.** An intake item that can't find a spec node gets no shortcut into work —
it goes the long way (idea → spec) so the requirement *exists* before the fix does. A bug is
the common case where a spec node usually already exists (the behaviour was specified, it's
just broken); a wish/new-capability usually doesn't, and becomes an idea.

## Process order — the fix never precedes the requirement

The chain above is **temporal, not just structural**. Implementation of new behaviour does
not start until the chain exists: accepted idea → spec node → work task (`InProgress`). The
work task is created **before** the code; `commits[]` land when it reaches `Review`.

Creating the chain retroactively — ship first, then backfill idea/spec/work — is a
**violation even though the result looks identical**: the board's integrity checks are
structural (edges), so back-dated edges are indistinguishable from honest ones, and nothing
couples git/CI/deploy to board state. Holding the order is therefore on the agent and the
maintainer, not on the engine. Two incidents (2026-06: entity-delete, and the pattern it
exposed across earlier milestones) are why this section exists.

Corollary for plan-mode (or any externally-approved plan): a plan's approval is NOT the
idea-accept. If a plan introduces new behaviour, the idea's `review → accepted` gate is a
**blocking step before implementation**, sequenced in the plan itself — not parallel
bookkeeping.

*(Altitude note: this section is an invariant of THIS project's methodology instance — the
hardcoded preset we dogfood. It lives here, in the instance's canon, deliberately NOT in the
PetBox product spec: the spec only promises that a project's methodology is the source of
its process truth and births the agent artifacts — see `user-methodology` in the spec tree.)*

## Iterations

An iteration = a **filtered backlog** (pull a batch by: no blockers, priority,
connectivity, simplicity, clear statement; plus carry-overs that failed
review/tests/approval). No sprint ceremony / story points. It closes with a
**release** whose artifacts are: test results + version tag + build + deploy target
(= our CI `ci.NNN` + `commits[]` + deploy).

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
- **Lifecycle & gate (enforced in code):** a spec node is born `defined` (a worked-out
  requirement) and can only retire to `deprecated` — there is NO `draft`/in-flux status
  (undefined thinking lives in an Idea, not the spec tree). Every spec create / change /
  deprecate requires **`ideaRef` → an `accepted` idea** (which auto-creates the `idea_spec`
  edge); a spec write with no accepted idea is rejected. You cannot touch the spec without
  going idea → accept first.

## It rides on what PetBox already has
- Spec = temporal tree ← `TemporalStore` (SCD-2).
- Iteration = release ← CI `ci.NNN` + `commits[]` + deploy.
- Intake ← the `incoming` phase + `report_issue`.
- `type=auto` ← the agent already classifies incoming requests reliably.

## Adoption status

This is the target model. It is adopted incrementally; until a feature is built,
the corresponding convention applies on the current Tasks primitives (e.g. separate
`spec`/`backlog` boards, the approve gate by convention). See the roadmap in the
`$system/roadmap` board (`mcp-typing` and methodology phases) and `doc/decision-log.md`.
