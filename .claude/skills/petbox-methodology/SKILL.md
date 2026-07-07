---
name: petbox-methodology
description: >-
  Operate PetBox's own project methodology — the idea → spec → work pipeline and its gates —
  on the `$system` project via the `petbox` MCP server. Use when creating or refining IDEAS,
  writing or changing the SPEC, creating WORK tasks, triaging INTAKE, or planning a PetBox
  module/feature. Encodes the gate rules (an idea needs a spec_plan artifact to reach review;
  the maintainer accepts; the spec is defined-only and every write needs ideaRef→an accepted
  idea; the agent never self-sets Done/accepted), the spec-writing format, and the exact MCP
  tool sequences + gotchas. Canon: doc/methodology.md.
---

# PetBox methodology — operator's guide

How to run work on PetBox itself through its own methodology. The canonical write-up is
`doc/methodology.md`; this is the **operator's cheat-sheet** (the agent-facing flow, exact
tool calls, and the gotchas that bite).

**Tool naming:** in opencode these MCP tools are `petbox_<verb>` (e.g. `petbox_tasks_upsert`,
`petbox_comments_upsert`); in Claude Code they are `mcp__petbox__<verb>`. The verbs written below
are the logical names — prefix them per runtime.

State lives on the `$system` project — query it,
don't assume: `tasks_methodology_get($system)` for the quartet, or
`tasks_get($system, <board>)` per board. Boards: `ideas`, `spec`, `work`, `intake` (the
methodology quartet, per-project singletons), plus `free` boards for scratch.

Core principle: **thinking must not be lost** — the asset is the reasoning, not the verdict.
Two gates protect quality: an idea is **accepted** only by the maintainer (= approval of a
spec change), and a work task reaches **Done** only by the maintainer.

## The pipeline (idea → spec → work)

### 1. IDEA (board `ideas`) — free-form deliberation
- An idea is **free-form**: dump anything, technical language, multiple wants. NO format
  rules apply here (the discipline kicks in at the spec stage).
- FSM: `raw → exploring → review → accepted` (mirror of work; `review → exploring` reopens;
  `review|exploring → rejected` with a reason). **There is no direct `exploring → accepted`.**
- **The agent's ceiling is `review`, NOT `accepted`.** Drive an idea up to `review` and STOP
  for the maintainer.
- **GATE: `exploring → review` is REJECTED without an `artifact:spec_plan` comment** on the
  idea — a `comments_upsert` with `tags:["artifact:spec_plan"]` stating the concrete spec
  changes (which nodes, the requirement text, and the implementation sketch that will go to
  work). No plan, no review.
- The **maintainer** does `review → accepted`. That accept = approval of the spec change-set.

Driving an idea to review (the agent's job):
```
tasks_upsert($system, ideas, [{key, type:"idea", status:"exploring", title, body}])      # or raw→exploring
comments_upsert($system, ideas, items:[{nodeId:<idea nodeId>, author, body:<the plan>, tags:["artifact:spec_plan"]}])
tasks_upsert($system, ideas, [{key, version:<v>, status:"review"}])                       # guard checks the spec_plan
# STOP — ask the maintainer to accept (or send back). Give them the idea's `url`
# (include_url:true) so they can open it directly — don't hand over a bare slug.
```

### 2. SPEC (board `spec`) — the requirements tree, written ONLY under an accepted idea
- **GATE: every spec node create/change/deprecate REQUIRES `ideaRef`** → the NodeId of an
  **`accepted`** idea. The service verifies it and auto-creates the `idea_spec` edge; with no
  accepted idea the write is **rejected**. You cannot touch the spec without idea→accept.
- **Lifecycle: a spec node is born `defined`** (a worked-out requirement) and can only retire
  to `deprecated`. **There is no `draft`** — undefined/in-flux thinking is an Idea, not a
  spec node.
- **Format (terse-normative «Должен», EARS-lite + RFC 2119):** the node **title** is the
  capability; the **body** is one normative line with the obligation + condition/consequence,
  or empty. Keywords: ДОЛЖЕН / СЛЕДУЕТ / МОЖЕТ. Functional → tag `area:*`; non-functional /
  invariant → `concern:*`.
- **Altitude:** a spec node is a **promise that survives reimplementation**. The mechanism
  (data shape, validation rules, API verbs, scopes, tree-vs-flat) is NOT a requirement — it
  goes in the work task. Test: *"would this change if we reimplemented without changing the
  promise?"* Yes → work task; no → spec.
- **Atomic but few:** one requirement per node, but at the owner altitude there are only a
  handful. An umbrella node + a few leaves (`partOf`). Don't pre-atomize implementation.

```
# under an accepted idea (ideaRef on EVERY node):
tasks_upsert($system, spec, [
  {key:"feature-x", type:"spec", status:"defined", title, body, tags:["area:..."], ideaRef:<accepted idea NodeId>},
  {key:"req-a", partOf:"feature-x", type:"spec", status:"defined", title, body, tags:["concern:..."], ideaRef:<same>},
])
```

### 3. WORK (board `work`) — implementation
- **Create the work task BEFORE writing any code** (status `InProgress` while you work).
  The chain accepted-idea → spec → work must exist before implementation starts — see the
  process-order gotcha below.
- A work `feature|bug` **REQUIRES `specRef`** → a spec NodeId (creates the `task_spec` edge).
  No spec link → rejected.
- **Link a task to EACH requirement it delivers (M:N)**, not just the umbrella — else the
  leaves read `not_started` and per-requirement `delivery` is hidden. Use `specRef` for one +
  `relations_create(kind:"task_spec", from:<task>, to:<leaf>)` for the rest.
- FSM: `Pending → InProgress → Review → Done` (+ Blocked/Deferred/Cancelled).
  **The agent's ceiling is `Review`; the maintainer confirms `Done`.** Move to Review when
  implemented + tests green + deployed; STOP — and give the maintainer the task's `url`
  (include_url:true) so they can open it to confirm Done.
- `delivery` on a spec node is **computed** from linked tasks (rolls up the `part_of`
  subtree): `not_started | in_progress | done | done_with_defects`. `status` (defined) =
  agreed; `delivery` = built — two different signals.

### INTAKE (board `intake`) — deferred triage, NOT a mandatory gateway
- Raw observations not yet routed: bugs, questions, wishes. NOT part of the pipeline.
- Use intake when the routing is unknown or you don't want to route now (noticed mid-task —
  park it). When the destination is already obvious — the maintainer reports it, or your
  diagnosis is clear — SKIP intake and create the node at the destination: hygiene → work
  `chore` (spec-less, no specRef); a bug violating an EXISTING spec node → work `bug` +
  specRef; no spec reflection → idea. Destination rules protect the chain by themselves.
- Triage → **reject** (reason) | **promote to a task** (ONLY if a matching spec node already
  exists — link specRef + `issue_task`; spec-less hygiene → `chore`) | **escalate to an idea**
  (NO spec reflection → the requirement was never specced → idea → gate → spec → work). An
  item with no spec gets **no shortcut into work** (work feature/bug needs specRef).

## Hard gotchas
- **THE FIX NEVER PRECEDES THE REQUIREMENT — no code before accept.** Implementation of new
  behaviour starts ONLY when the chain exists: accepted idea → spec node → work task
  (`InProgress`). The work task is created BEFORE the code; `commits[]` land at `Review`.
  Backfilling the chain after shipping is a violation even though edges look identical
  (integrity checks are structural, not temporal — two incidents prove it slips through).
  In plan-mode: a plan's approval is NOT the idea-accept; if the plan introduces new
  behaviour, sequence the maintainer's accept as a BLOCKING step before implementation,
  not as parallel bookkeeping. (Instance invariant of THIS project's methodology — canon
  `doc/methodology.md` § "Process order"; the product spec only carries the meta-promises,
  see spec `user-methodology`.)
- **Agent never self-accepts an idea or self-sets a work task to Done.** Stop at `review` /
  `Review` and hand to the maintainer. (Enforcement: spec_plan guard is in code; the
  accept/Done approval is convention — don't abuse it.)
- **Deleting an erroneous node: `tasks_upsert` with `{key, deleted:true}`** (soft
  temporal-close, history kept; children first or the whole subtree in one batch; spec-node
  deletes need no ideaRef — erasing junk is not a spec change, retiring a requirement stays
  `deprecated`). Sessions: `session.delete`. Avoid creating junk that needs deleting.
- **Don't write the spec in `draft` or without `ideaRef`** — both are rejected now. If you
  catch yourself wanting to "dump a spec subtree", you skipped the idea→accept gate.
- **Spec body = requirement (promise), not implementation.** If your spec node says "table X,
  M007 migration, ParentId column", it's at the wrong altitude — move that to the work task.
- **Process order:** write the `spec_plan` artifact BEFORE editing the spec. The artifact is
  the maintainer's review object; the spec is applied only after accept.
- **`methodology_get` is a compact INDEX** (no node bodies by default — just identity,
  status, title, tags, links, `delivery`, plus a per-board status histogram `counts`). It's
  the cheap orientation call; reach for it first. Pass `bodyLen:<N>` for a per-node body
  snippet (first N chars, `…` when cut; large N ≈ full) and `includeBoards:["spec","ideas"]`
  to fetch only some quartet boards. For full untruncated bodies or a subtree, use
  **`tasks_get`** (the single-board detail endpoint: `under:<slug>` / `groupBy`).
- **MCP result bodies** can still be large on `tasks_get` (a board can be 60k+ chars) — pass
  a high `sinceVersion` or use `under:<slug>` / `groupBy` to keep deltas small; null fields
  are omitted in JSON.

## Tools (petbox MCP)
- `tasks_methodology_get` / `tasks_get` / `tasks_workflow` (read) · `tasks_upsert` (write:
  status/body/partOf/tags/specRef/ideaRef/supersedes) · `tasks_board_create|close|reopen`.
- **`include_url:true`** on `tasks_methodology_get` / `tasks_get` / `tasks_upsert` / `tasks_delta`
  adds an absolute `url` permalink to each returned node (its `/ui/.../tasks/node/{nodeId}`
  detail page) — off by default.
- **CONVENTION — surface the link:** whenever you create a node the user should see, or you
  need a decision from them on one (drive an idea to `review`, a work task to `Review`, ask
  them to accept / set Done / pick between nodes, or say "look at X"), pass `include_url:true`
  and show the returned `url` as a clickable markdown link on the node title — never hand over
  a bare slug. The maintainer acts from the UI; a direct link is the shortest path to the
  thing they must decide on.
- `comments_upsert|search|get|delta|delete` — the deliberation thread under any node (upsert is
  a batch of {id?, nodeId?, parentId?, author?, body, tags?, version?} items; id-null = create,
  id-set = patch under a version watermark); `artifact:<slug>` tags mark key artifacts
  (`artifact:spec_plan` is the gate precondition).
- `relations_create|list|delete` — kinds `idea_spec | task_spec | issue_task | blocks |
  part_of | supersedes`.

## Doing it well
Go slowly; respect the gates. Treat the run itself as the test of whether the methodology is
usable — if an invariant gets in the way awkwardly, that's a finding (a work `chore` when the
fix is obvious, an intake issue when routing is unclear, or an idea like
`node-hard-delete`/`methodology-fsm-gaps`), not something to paper over. Dogfood:
record deliberation as comments on the idea/task; keep the spec lean and the work task fat.
