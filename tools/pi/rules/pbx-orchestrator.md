## Orchestrating a PetBox project — [own]

These rules exist because you are the main session of a project wired to PetBox: you
hold the plan, the ids and the verdict, and your context is the one that runs out first.
They apply to you only — a leaf role has no tools to act on them.

- **Delegate by default.** A read-only conclusion — locate, map, summarise — goes to a
  read-only role. A second file to touch, or any change at all, goes to a writer. The
  only work you keep is what is obviously elementary (one lookup, one line, one answer
  you already have). Shorten the brief, never the role.
- **Ask for the verdict, not the material.** Your window holds the plan, ids and
  conclusions — never file bodies, logs or diffs. Read bytes yourself only to judge them.
  A context burning down is proof you delegated too little.
- **Facts before a checker.** Run the scouts and workers first, then hand over the claims: a
  `reviewer` or `oracle` briefed to find out what you skipped pays a smart model for your
  research. It may look further on its own; widening past the brief still needs the owner.
- **The tier is the role; effort is the knob.** Do not pass a `model` argument to change
  how smart a spawn is — pick a different role instead (`scout` for reading, `worker` for
  writing, `oracle` when the answer must be attacked rather than produced). The one
  sanctioned per-call override is an effort suffix on the same model (`:max`), for work
  where a wrong result would be expensive or would pass unnoticed.
- **A card keeps its worker.** The agent that built a change is the one that fixes its review
  findings, its failing test or its follow-up question — resume it rather than spawning a fresh
  one that has to learn the same files again. Same card, same agent; the mechanics are in
  `amd-delegation`.
- **Reserve triggers on events, not on feeling stuck:** facts killed your hypothesis, you
  are about to retry a failed approach, or two defensible designs remain with a costly
  rollback. Hard-but-moving work still goes to a writer.
- **Never accept a verification you did not see.** "Still running, will report" reports
  nothing — re-run it yourself. Tools also lie about remote state: confirm against the
  live system, not against the tool that says it shipped.
- **Never self-set `Done`/`accepted`.** Move the card through its statuses and stop one
  short, then hand over. Only the owner's explicit call lifts that.
- **A finding becomes a card by promotion, never by hand.** A defect-like discovery — broken,
  unexpected, contradicts the docs or the process — is a node on the project's `observations`
  board, and a finding that needs work is turned into that work with
  `tasks_observation_promote`. The promote writes the `observation_obligation` relation, the
  observation flips to `fixed` when the obligation reaches a terminal-ok status, and a later
  recurrence reopens it to `seen` and flags the task that had fixed it. Creating a `work` or
  `ideas` card by `tasks_upsert` *instead* breaks that chain: two nodes for one finding, no
  relation between them, and nothing to close the loop when the work is done. A repeat sighting
  bumps the existing observation; it never gets a second node.
- **A card grows by comments.** Progress, findings and corrections go on as `comments_upsert`:
  the body states the intent, the thread keeps the history.
- **Sweep stale `InProgress`.** Periodically list them oldest-first by `updated` and
  either finish, cancel, or re-confirm each one. A card parked in its starting status is
  a defect, not a backlog item.
- **Take a second reading on signal** — "or", "either", "at least", "however reasonable",
  or you picked one of several plausible options. Name the assumption and check it before
  it becomes the plan.
- **Name what you did not verify.** A claim you did not re-run, a doc you did not open, a
  state you inferred: say so in the report. Silence reads as confirmation.
