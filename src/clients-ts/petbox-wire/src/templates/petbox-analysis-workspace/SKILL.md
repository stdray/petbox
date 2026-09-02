---
name: petbox-analysis-workspace
description: >-
  Run a voluminous, multi-part investigation (a cross-cutting audit, a large refactor
  survey, "what's the state of X across the codebase") as staged files in an external
  working folder instead of hundreds of MCP/tool calls or a sprawling chat transcript.
  Use when the task has multiple independent areas to cover, needs a fan-out of several
  subagents, or would otherwise blow the context budget re-deriving the same ground truth
  repeatedly. Not for a small task, a single file, or a short question — see "When not to
  use this" below. Never automatic — the caller invokes it on purpose.
petbox: managed
petbox-digest: manual
disable-model-invocation: true
---

# Analysis workspace — staged files over repeated calls

For a big, many-part investigation, do the thinking in **numbered files in a working
folder outside the repo**, not in chat turns or by re-querying the same sources hundreds
of times. Files are cheap to read, cheap to diff, cheap to hand to a subagent, and — unlike
a chat transcript — they can be *edited in place* as understanding improves.

## The stages

Artifacts evolve through stages, each building on the last. Don't skip straight to a
summary — the earlier stages are what make the summary trustworthy:

1. **Legend / terms** — pin down vocabulary before anyone writes findings. A word used in
   two senses will silently corrupt every later stage (see Pitfalls).
2. **Snapshot of current state** — what's actually true right now, dated, sourced.
3. **Per-area inventory** — one file per area/participant/subsystem, written by whoever
   (or whichever subagent) covers that area.
4. **Consolidated summary** — a single pass that reads all inventories together.
5. **Decision registry** — open questions converted into decisions (see below).
6. **Pre-cards** — small, near-final write-ups staged for promotion into the real
   tracking system.

Put an **index file** at the root of the working folder that says which file to read
first. A folder full of numbered files nobody can navigate is worse than a chat log.

## What worked

- **Fan-out with a shared format file.** Before subagents start, write the file that
  defines the shape every per-area inventory must have, and have every subagent read it
  first. Without this, N inventories come back in N incompatible shapes and the
  consolidation pass has to normalize before it can compare.
- **Each subagent writes its own file.** Parallel subagents touching the same file
  collide; one file per agent avoids that entirely and makes provenance obvious.
- **Hard line limits + "don't paste the file content back."** Tell each subagent a line
  budget for its output file, and explicitly forbid it from repeating the file's content
  in its chat response. The orchestrator's context is the scarce resource; the file
  already has the content.
- **A dedicated consolidator told to surface contradictions, not average them.** Give one
  agent (or pass) the job of reading all inventories side by side and instructed
  explicitly to flag disagreements between files — including disagreements with the
  summary itself — rather than quietly picking one version or blending them into mush.
  Contradictions are signal; a smoothed-over summary destroys it.
- **A decision registry, not a question list.** Each open item gets a **decision**, its
  **cost**, and **what breaks if it's not adopted** — not just "should we do X?". A
  registry of unresolved questions doesn't move anything forward; a registry of decisions
  with consequences does.
- **Pre-cards with a real promotion bar.** A pre-card qualifies only if it (a) can be
  acted on now and (b) still makes sense after a model/agent swap mid-task — i.e. it
  doesn't depend on context that only lived in one session's head. Run a separate
  consolidation pass to merge cards that are too small to verify without their neighbors;
  a pile of micro-cards is not more actionable than one no card at all.

## Pitfalls hit in practice

- **Artifacts go stale.** A finding can be closed out by the owner days or weeks later
  while the file still asserts it's open. Date every file, and when re-checking a claim,
  check it against `origin/main` (or whatever the shared truth is), not against a local
  working tree that may itself be stale or ahead.
- **Partial files disagree with each other, and with the summary.** This is expected at
  scale, not a sign of failure — it's why the dedicated contradiction-surfacing pass
  exists. Budget for finding several real contradictions, including one inside the
  "final" summary itself.
- **A decision edit doesn't propagate on its own.** Fixing the registry entry does not
  update the N other files that quoted the old formulation. Run an explicit propagation
  pass after any decision change — grep the working folder for the old wording.
- **A subagent's report is not proof.** Subagents (and orchestrators) both assert things
  that don't hold up against the actual code, and both catch real errors the other
  missed. Verify empirically — the report describes what the agent *believes* it did, not
  a checked fact.
- **The framing itself can be wrong even when every individual fact is right.** Twice in
  one investigation, a whole classification scheme had to be thrown out and restarted —
  the facts were fine, the axis they were sorted along wasn't. The tell is qualitative:
  when the person driving the task says something like "I feel like I'm doing busywork,"
  treat that as a signal the *foundation* is broken, not that more data is needed.
- **Terminology collisions poison downstream artifacts.** One word carrying two meanings
  will corrupt a decision registry or a questionnaire before anyone notices — this is why
  the legend/terms file comes first, not last.

## When not to use this

Skip the whole pattern for a small task: one file to check, a short factual question, or
anything answerable in a couple of direct reads/calls. The overhead of a working folder,
an index, and staged files only pays for itself once the task has enough independent
parts that losing track of any one of them is a real risk.

## Relation to PetBox methodology

The working folder is a **draft space** — nothing in it is authoritative. Only the
consolidated, ratified cards get promoted onto real boards (see the `petbox-methodology-system`
skill for the idea → spec → work gates); code is never written ahead of an accepted card.
Treat the working folder the same way you'd treat scratch notes before a spec_plan
artifact: useful for thinking, not itself a source of truth.
