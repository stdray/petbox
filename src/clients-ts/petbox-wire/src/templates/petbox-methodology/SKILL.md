---
name: petbox-methodology
description: >-
  Learn THIS project's actual task-methodology (if any) before creating or moving nodes on its
  idea/spec/work/intake boards. Use before writing an idea, defining a spec node, opening a work
  task, or triaging intake. The gate rules — which boards exist, which transitions are blocked,
  which artifacts or links a gate requires — are PROJECT-SPECIFIC and fetched at runtime here,
  never assumed from another project.
petbox: managed
petbox-digest: auto
---

# PetBox methodology — read the live rules, don't assume them

PetBox lets a project turn on an optional gated process (idea → spec → work, or a custom set of
boards) and pick a preset for it — `quartet`, `classic`, `simple`, a hand-tuned custom instance,
or none at all. Which one (if any) `{{PROJECT}}` runs is **not fixed by this skill** and can
change over time. **Never carry over methodology rules you learned on a different project —
including one you may have seen documented elsewhere — they were not agreed here, and this
project's gates will reject your writes for reasons you never looked up.**

## Before touching `ideas` / `spec` / `work` / `intake` boards

Call the guide first, every session — rules can be edited by the project owner between sessions:

```
tasks_methodology_guide(projectKey:"{{PROJECT}}")
```

(`petbox_tasks_methodology_guide` in opencode, `mcp__petbox__tasks_methodology_guide` in Claude Code.)

- **No open methodology instance** → the response falls back to a generic preset baseline
  (`source:"presets"`) purely as orientation; nothing is actually enforced. Treat the boards as
  free-form until an instance exists (`tasks_methodology_create`), and don't invent gates.
- **An open instance** → `markdown` is the narrative guide for THIS project right now, and
  `invariants` is the same thing machine-readable. Read both before you write anything.

## Reading `invariants`

Each entry is `{ kind, rule, detail }`. `kind` is the board/type the rule applies to (an
idea-kind invariant doesn't gate a work-kind transition, and vice versa). `rule` tells you what
to check for — don't assume any of these are absent just because you haven't seen them fire yet:

- `approval_gate` / `approval_gate_enforced` — **default-deny:** never set a terminal ok status
  (`Done`/`accepted`-like) yourself. Exactly two exceptions, both external to you — never derive
  a right to close from your own reading of `invariants`: (1) this guide states explicitly that
  the kind has no approval gate (see the GATES section's "No approval gate…" line) — then the
  executor sets the terminal status; (2) the project owner explicitly authorized it, by direct
  instruction or a standing directive. Otherwise stop at the status immediately before the gated
  one (e.g. `Review` before `Done`) and hand over — read it off this guide's `invariants` every
  session, never from memory. `_enforced` means the server itself rejects the agent's own attempt
  at the gated transition; the plain form is SOFT — the server does not block it, it holds only
  because the agent honors it.
- `precondition_artifact` — a transition requires a tagged comment (an "artifact") to already
  exist on the node; `detail` names the tag.
- `reason_required` — the transition call must carry a reason string.
- `link_constraint` — a create or transition requires a specific link (e.g. a reference to an
  accepted node on another board) or it is rejected outright.
- `checklist` — preconditions to verify before attempting the transition.
- `transition_effect` — a side effect fires on this transition (e.g. it auto-closes a linked
  node elsewhere).
- `tag_axes` — required tag-prefix axes for a kind (e.g. `area:*` / `concern:*`); tags aren't
  free-form where this applies.

## Tools

- `tasks_methodology_guide` — call this first; resolves the project's active instance, or pass
  `key` (the instance's slug) for one specific instance.
- `tasks_methodology_rules_get` / `tasks_methodology_list` — the raw rules document / instance
  index, for when the guide's rendering isn't enough. `_list` hands back each instance's `key`;
  that same string is what every `tasks_methodology_*` verb takes as its `key`.
- `tasks_methodology_get` / `tasks_search` / `tasks_node_get` / `tasks_workflow` — read
  boards/nodes once you know the shape from the guide above.
- `tasks_upsert` / `comments_upsert` / `relations_create` — the writes the gates above govern.

Addressing: a methodology INSTANCE is addressed by `key` (its slug) on every verb — never by
`name`. `name` on this surface means a document's human-readable title and addresses nothing.

Tool naming: base verbs are underscore-delimited (`tasks_methodology_guide`); opencode prefixes
`petbox_`, Claude Code prefixes `mcp__petbox__`.

## If a write gets rejected

A rejection here is the methodology working, not a bug. Re-read the guide's `invariants` for the
`kind`/`rule` you tripped, supply the missing link/artifact/reason, and retry. If it still
doesn't make sense after reading the guide, that itself is worth reporting — through this
project's own intake/triage path, or to its maintainer — rather than working around it.

## Writing a spec node (if this project has a `spec` board)

- **Format (terse-normative, EARS-lite + RFC 2119):** the node **title** is the capability; the
  **body** is one normative line stating the obligation plus its condition/consequence, or
  empty. Keywords: MUST / SHOULD / MAY (or this project's own language for the same three
  strengths). Tag functional requirements with one axis, non-functional/invariant ones with
  another — check the guide's `tag_axes` invariant for the actual prefixes this project uses.
- **Altitude:** a spec node is a promise that survives reimplementation. The mechanism (data
  shape, validation rules, API verbs, storage layout) is NOT a requirement — that belongs in the
  work task. Test: *"would this change if we reimplemented without changing the promise?"* Yes →
  work task; no → spec.
- **Atomic but few:** one requirement per node, but at the owner altitude there are usually only
  a handful — an umbrella node plus a few leaves. Don't pre-atomize implementation into the spec.

## Triaging intake (if this project has an `intake` board)

Intake holds raw, unrouted findings — bugs, questions, wishes — not yet placed on the pipeline.
Skip it when the destination is already obvious (the report names it, or the diagnosis is clear)
and create the node at the destination directly instead of parking it:
- Spec-less hygiene → a work `chore` (no spec link needed).
- A bug against an EXISTING spec requirement → a work `bug`, with whatever spec link this
  project's `link_constraint` invariants require.
- Nothing in the spec reflects the ask at all → an idea, so it goes through the idea→accept→spec
  gate before any work node is opened for it.
An intake item with no matching spec never gets a shortcut straight into work — a `feature`/`bug`
work node still needs the spec link its own invariants demand.

## The observations board — platform-wide, present in every project

`observations` is a system-built-in board, auto-created per project, and lives outside this
methodology instance's gates (it never enters the owner's decision queue or digest). The regular
task tools apply as-is: `tasks_search`, `tasks_node_get`, `tasks_upsert`, `tasks_delta`,
`comments_*`.

- **Status is a value, not an FSM:** `seen` (open) → `promoted` (open) → `fixed` (terminal ok);
  `declined` (terminal cancel).
- **Dedup with recurrence, on every write:** a similar finding landing on this board — automatic
  or a manual `tasks_upsert` — does not create a duplicate. It bumps the existing node's
  `recurrenceCount`/`lastSeenAt` instead, reported back as `deduped:[{requestedKey, existingKey,
  existingNodeId, recurrenceCount}]`. Only fires on a purely-creating batch (every node
  `version:0`, no deletes) — don't mix creates with edits in one call.
- **Promotion — `tasks_observation_promote`:** turns a `seen` observation into a real `work` task
  (`type` required: `feature|bug|chore`) or an idea (`targetBoard:"work"|"ideas"`, plus
  `key`/`title`/`body`/`links`/`tags`/`sessionId`). Creates an `observation_obligation` relation
  (visible in `relations` from both sides) and moves the observation to `promoted` — it stays
  addressable, it does not disappear.
- **Fix-pinning, automatic:** the linked obligation reaching a terminal-ok status flips the
  observation to `fixed` (stamps `fixedByNodeId`/`fixedAt`); a terminal-not-ok status reopens it
  to `seen` — the problem wasn't fixed, it was abandoned.
- **Regression detector:** a fresh hit of the same problem after a fix reopens the observation to
  `seen`, stamps `recurredAfterFixAt`, surfaces it higher in search, and sets
  `decisionPending:true` on the task that had "fixed" it — the task's own terminal status is
  **not** reopened automatically, an owner call, to keep cycle-time metrics honest.
  `recurrenceCount`/`lastSeenAt`/`recurredAfterFixAt`/`fixedByNodeId`/`fixedAt` all ride the
  `observation` field on search/node-get hits.
- A defect-like finding (broken, unexpected, contradicts docs, a process defect) goes straight
  onto this board yourself — never into memory, never into a generic intake bucket. It dedups by
  recurrence, so "already observed" is never a reason to stay silent.

## Practical MCP gotchas

- **Soft-delete:** `tasks_upsert` with `{key, deleted:true}` is a temporal close, history kept —
  delete children first, or the whole subtree in one batch.
- **`bodyLen`:** most read tools omit node bodies by default (a compact index only — identity,
  status, title, tags, links). Pass `bodyLen:<N>` for a per-node body snippet (first N chars,
  `…` when cut; a large N is effectively the full body).
- **Response size:** a board detail call can return a large payload (tens of thousands of
  characters for a busy board). Pass a high `sinceVersion`, or narrow with `underNode:<slug>` /
  `groupBy`, to keep the response small; null fields are omitted from the JSON either way.
