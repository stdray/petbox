---
name: petbox-methodology
description: >-
  Learn THIS project's actual task-methodology (if any) before creating or moving nodes on its
  idea/spec/work/intake boards. Use before writing an idea, defining a spec node, opening a work
  task, or triaging intake. The gate rules — which boards exist, which transitions are blocked,
  which artifacts or links a gate requires — are PROJECT-SPECIFIC and fetched at runtime here,
  never assumed from another project.
petbox: managed
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
- `tasks_methodology_get` / `tasks_get` / `tasks_workflow` — read boards/nodes once you know the
  shape from the guide above.

Addressing: a methodology INSTANCE is addressed by `key` (its slug) on every verb — never by
`name`. `name` on this surface means a document's human-readable title and addresses nothing.
- `tasks_upsert` / `comments_upsert` / `relations_create` — the writes the gates above govern.

Tool naming: base verbs are underscore-delimited (`tasks_methodology_guide`); opencode prefixes
`petbox_`, Claude Code prefixes `mcp__petbox__`.

## If a write gets rejected

A rejection here is the methodology working, not a bug. Re-read the guide's `invariants` for the
`kind`/`rule` you tripped, supply the missing link/artifact/reason, and retry. If it still
doesn't make sense after reading the guide, that itself is worth reporting — through this
project's own intake/triage path, or to its maintainer — rather than working around it.
