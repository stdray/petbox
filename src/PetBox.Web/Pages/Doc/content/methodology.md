# Methodology cheatsheet (agent)

How to drive the spec / work / idea rails. This is the **operational contract**; the reasoning behind it is on the [philosophy page](/doc/methodology/philosophy). Call `tasks_workflow(projectKey, board)` any time to get the live statuses/transitions for a board.

## Boards have a `kind`

Set `kind` on `tasks_board_create` (it can't be changed later). The kind drives which task types, statuses, transitions and invariants apply. Default is `simple`.

- **`spec`** — the requirement tree (only DEFINED requirements). Node status `draft|defined|deprecated`. Each node also carries a COMPUTED `delivery` (see below).
- **`work`** — technical tasks, type `feature|bug|chore`. Status `Pending→InProgress→Review→Done` (+ `Blocked|Cancelled`). A feature/bug MUST link a spec node (see spec-link); `chore` — internal engineering hygiene (tests, flakes, refactoring, infra) — shares the FSM but needs no spec link.
- **`ideas`** — deliberation, type `idea`. Status `raw→exploring→{rejected|deferred|accepted}`.
- **`intake`** — raw issues, type `issue`. Status `reported→triage→{confirmed|duplicate|wontfix}→done`.
- **`simple`** — a lightweight preset for ad-hoc/scratch work: type `task|bug|feature|chore|issue`, status `Todo→InProgress→Done` (+ `Blocked|Cancelled`) with FREE transitions (any valid status → any, no gates), and free-form tags. No spec/idea governance.

**Standard boards.** A project uses one board of each kind, named for its kind: `ideas`, `spec`, `work`, `intake` (+ `simple` scratch). Use those names so every agent and session finds the same boards.

**The flow starts in `ideas`.** A piece of work begins as a raw idea; deliberating it (raw→exploring→accepted) is what *produces* the spec entries — the accepted idea's conclusions become `spec` nodes, and `work` features implement those. Don't invent spec out of thin air; let it fall out of an idea. (Capturing the deliberation itself — raw idea / worked-out statement+decisions / spec-update plan — as a discussion thread on the idea is coming; for now keep it in the idea's `body`.)

## Addressing a node

Every node has a **flat slug** `key` (`^[a-z][a-z0-9_-]{0,99}$`, board-unique). Hierarchy is the `partOf` field (a parent slug or nodeId); cross-cutting grouping is `tags`. The slug is a stable anchor you cite (it survives content edits; rename via `prevKey`). Give each node a short `title` and a markdown `body`.

```
tasks_upsert board="spec" projectKey="<proj>" nodes=[
  { "key":"auth", "status":"defined", "title":"Auth" },
  { "key":"login", "partOf":"auth", "status":"defined", "title":"Login flow" }
]
```

## Links (relations)

Edges bind to the stable `nodeId` (in every upsert/read response), so they survive renames. They are temporal — soft-closed, not deleted (history kept; `relations_list ... includeHistory=true`). `tasks_search` surfaces them inline per node: `spec` (what a task implements), `blockedBy`, and on spec nodes `linkedTasks` — each resolved to its board + slug + title.

**Finding the spec board:** set it once per work board with `tasks_board_set_spec` (or `specBoard` on `board_create`); then `tasks_search`/`board_list` report it as `specBoard` and `specRef` is validated to point at that board. No mapping = the agent picks any `kind=spec` board and links by nodeId.

- **`task_spec`** — set `specRef` (a spec node slug or nodeId) on a work feature/bug: links it to the spec node it implements. **Required** for a new work feature/bug (spec-link invariant — no work without a spec node); `chore` is exempt. The target is validated: it must be an existing node on a spec board (and on the board's `specBoard` if set).
- **`blocks`** — set `blockedBy` (a nodeId) on a work task you move to `Blocked`. A Blocked task MUST name a blocker.
- **`issue_task`** — a confirmed intake issue spawns a work task; link them so the issue auto-closes when the task is done.
- **`idea_spec`** — an accepted idea produced this spec node/version.

## Effects (run automatically)

- A work task → `Done` auto-closes any intake issue linked via `issue_task`.
- A blocker → `Done` closes its `blocks` edges; a blocked task with no remaining blockers auto-moves `Blocked → InProgress`.
- A spec node's `delivery` is COMPUTED from the tasks linked to it and its subtree: `not_started` (no feature tasks) / `in_progress` (some feature not Done) / `done` (all features Done, no open bug) / `done_with_defects` (all Done but an open bug). You never set `delivery` by hand.

## Approve gate (convention)

An agent's ceiling on a work item is **`Review`** — mark a finished item `Review`, never `Done`. Only the maintainer confirms `Done`. (Enforced by convention today; the engine models it.)

## What goes where

- **idea** (ideas board) — thinking in flux, an outcome to reach. Fast capture, no spec link.
- **spec** (spec board) — a DEFINED requirement. Changing a requirement = a new idea → a new spec version.
- **work** (work board) — a technical unit that implements a spec node (feature/bug, always links spec) or internal engineering hygiene (chore, no spec link).
- **issue** (intake board) — a raw report; triage to confirmed, then spawn work.

Fast capture lives on ideas/intake (no spec link); committed work lives on the work board (spec-linked). That separation is the point — see the [philosophy](/doc/methodology/philosophy).

## Right-size the rails

Match the structure to the work — the full rails pay off on a long-lived, multi-agent project where the spec is the asset; don't over-ceremony a small one-off.

- **Scratch / spike / throwaway** → one `simple` board. No idea, no spec, lightweight statuses. Use when the work is exploratory or won't be maintained.
- **Small self-contained build** (a small game, a script, a single feature) → start with a *short* idea on `ideas`, let it settle into a few *thin* `spec` leaves (acceptance criteria, one line each), then `work` features linked (`specBoard` set). Skip `intake` until reports actually arrive.
- **Ongoing / multi-session / multi-agent project** → the full rails: `ideas` → `spec` → `work` + `intake`, with deeper deliberation and a real requirement tree.

A tier scales *how deep* the idea and how thick the spec are — not whether you start from an idea. Only pure throwaway scratch skips the idea entirely (a `simple` board). The spec-link invariant on `work` holds at every tier — it's the forcing function.

## Tools

- `tasks_board_create(kind?, specBoard?) / board_list / board_delete / board_set_spec / board_close / board_reopen` — boards (all report kind, specBoard, closed).
- `tasks_search / node_get / upsert / delta` — nodes (key, nodeId, parentSlug, depth, status, type, title, body, priority, version; plus surfaced links and on spec boards `delivery`). `search` is the one read verb: without `q` a deterministic listing (board-scoped responses carry the board `kind`), with `q` a hybrid relevance search; both modes take `status`/`keys`/`under` filters and `sort`. It hides terminal nodes by default — pass `includeClosed=true`. **Partial update:** a field omitted from `upsert` keeps its prior value, so a status change needs only the key + `version` + `status`.
- `tasks_workflow` — the live statuses/transitions for a board's kind.
- `relations_create / list / delete` — typed edges (task_spec|issue_task|idea_spec|blocks|nfr|dup).
- `report_issue` — file a PetBox bug/issue to the maintainer's intake.
