# PetBox methodology — the model

How we run a project so its state stays **surveyable** and **deliberation is durable** (thinking isn't lost when a session closes). The operational contract is on the [cheatsheet](/doc/methodology); this page is the why.

## Why

State used to smear across many places (a plan file, a board, parking, sessions, a decision log), hand-synced. Two pains:

- **Thinking gets lost.** Discuss-but-don't-execute (reject an idea, defer it, just explore) → the reasoning dies with the session and we circle back. The asset is the *reasoning, not the verdict*.
- **`Done` ≠ approved.** Agents marked work Done and shipped; the maintainer had no gate and kept reopening. Agents must not self-certify completion.

## Two interacting but separate processes

**P1 — requirements evolution.** Artifact: the **spec tree**. Unit of work = an **idea**: `raw → refined statement → plan to update the spec → new spec version`. A rejected idea never enters the tree — it stays a record "considered, rejected: why". Invariant: **only DEFINED requirements live in the tree**; changing a requirement = a new idea (same cycle).

**P2 — technical.** Artifact: **tasks**. Bridge from P1: a spec leaf with no linked tasks → create tasks → work board. Each task carries a type (feature|bug|chore), links its spec node (M:N; chore — internal hygiene — is exempt), and a provenance (originating issue + spec item).

## Entities

1. **Session** — raw transcript of one episode; source material.
2. **Idea / deliberation** — a thread of thinking distilled from sessions, with an outcome (open / exploring / rejected / deferred / accepted) + reasoning. Separate from the plan.
3. **Spec / feature** — a temporal tree of defined requirements (functional + non-functional). Its delivery status is COMPUTED, not set by hand.
4. **Task** — a technical unit on the work board; feature|bug|chore; feature/bug link a spec node (chore needs none); carries commits[].
5. **Relation** — typed temporal edges between nodes (task↔spec, issue→task, idea→spec, blocks). They bind to a stable id, so links survive renames, and they're soft-closed so history is kept.
6. **Intake** — a queue of raw issues → triage → {reject-with-reason | confirm → spawn task with links}.

## Task lifecycle + the approve gate

`backlog → (selected into an iteration) → InProgress → Review (agent finished) → tests → APPROVE (maintainer) → Done`; reject → back with a reason. **An agent never sets the final `Done` — its ceiling is `Review`.** The maintainer confirms.

## Computed spec status — bottom-up, type-aware

A spec node's `delivery` derives from its linked tasks over the subtree:

- no implementing (feature) tasks → **not started**;
- some feature not Done → **in progress**;
- all features Done, no open bug → **done**;
- all features Done, but an open bug → **done with defects** (a bug ≠ "not built").

Parents aggregate their children. **Surveyability:** the feature tree with computed status is the primary view; kanban/Gantt are projections over it.

## Blocked is a relationship, not a flag

A task is `Blocked` only when a real `blocks` edge names what blocks it. When the blocker reaches Done, the edge soft-closes and the task auto-unblocks — and the history ("blocked by X from T1 to T2") is kept.

## Fast capture vs committed work

Ideas and intake are the fast-capture surfaces (no spec link required) — a quick session lands there without ceremony. The work board is the spec-linked surface: to put a task there you organize the spec. That separation is what keeps the spec tree honest without making every session slow.

Adopted incrementally on the current primitives (board kinds, relations, computed status). See the cheatsheet for the live contract.
