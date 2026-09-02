---
name: petbox-card-check
description: >-
  Use before sending a work card to a worker — is the ask checkable (it names what closes
  it and what is out of scope) and is it the owner's ask rather than a paraphrase — and
  again before reporting Review or accepting a result: walk each promised bullet against
  the real diff (COVERED / NOT COVERED / EXTRA; partial is NOT COVERED) and say in one
  sentence whether the result does what the card is FOR. Never automatic — the caller
  invokes it on purpose.
petbox: managed
disable-model-invocation: true
petbox-digest: manual
---

# Card check — the ask, and the result against it

Process discipline holds on its own: instructions get followed, MCP calls get made, gates
get run. What slips silently is *content* — a card that never said what "done" means, or a
task finished cleanly that covers less than was asked, with no error anywhere to trip on.

**What this catches, and what it does not.** Run by the same head that did the work, the
bullet walk below reliably catches *"not fully done"* — a promise from the card simply
absent from the diff. On that same head it is weak on *"not the right thing"*, because the
head that misread the ask will misread it again; the FOR step is what catches that, and it
is worth far more on a *different* head than the one that wrote the code. Nothing here
triggers automatically — no status, no hook. The caller decides.

## Moment A — before the card is sent (the card's AUTHOR, not the worker)

You cannot check a result against a card that never committed to anything. Before a card
goes to a worker, it must carry, in the owner's own words:

- **What closes it** — concrete enough that its absence from a diff would be visible.
- **What is NOT in it** — the boundary, so scope creep and a gap are distinguishable later.
- **One sentence of what it is FOR** — what the owner can do afterwards that they cannot
  do now. This is the sentence the last step scores against; without it there is nothing
  to score.

Bullets must be the owner's ask, not your paraphrase of it. A paraphrase is where the gap
is introduced — quietly, before any code exists. **No such section: the card does not go
into work.** Fixing the card is cheaper than every step below.

## Moment B, entry — before writing any code (the worker)

Write down 3-5 bullets of what will be done, concrete and in the card's own language, plus
the card's out-of-scope list close to verbatim. Do this *before* coding: a misreading then
costs one short pass; after the work is done it costs the work.

## Moment B, exit — the mechanics

Three questions, each answered by a command whose raw output goes into the report
**verbatim**. The verdict is read off that pasted output, never recalled from memory —
remembering what you did is the failure mode this whole procedure exists to survive.

1. **Do the artifacts the card names exist**, at the paths and names the card used?
2. **Did the gate the card names actually run green** — the real exit code, never a claim
   in a chat message.
3. **Does the diff touch what the card named?**
   `git diff --numstat <base>...HEAD -- <paths from the card>`

Two things about that command, both learned the hard way:

- Compare on **`--numstat`, never `--stat`**. `--stat` abbreviates a long path to a
  `.../` prefix, so a file you did touch reads as untouched and a real gap is scored
  COVERED (commit `0080c198`). Print `--stat` for humans if you like; never compare on it.
- The range is **three-dot** `<base>...HEAD` — from the merge-base, so commits that landed
  on the base after you branched are not yours to explain. Base defaults to `origin/main`,
  which must be fetched first. If the diff command *errors*, that is a finding in itself —
  report it as one, never as "no diff".

## Moment B, exit — the bullets

Walk the entry list against the real diff. Each bullet gets exactly one word:

- **COVERED**
- **NOT COVERED** — including partial. Never soften a partial into "mostly covered".
- **EXTRA** — done beyond the card; check it against the out-of-scope list.

One line per bullet, flat. **If anything is NOT COVERED, that is the report's headline**,
quoted from the card — not folded into a summary that opens with "looks good".

## Moment B, exit — the FOR step

Take Moment A's one-sentence "what it is FOR" and answer it against the result: **yes or
no, plus one line of evidence**. Every bullet can be COVERED and the answer still be no —
that is precisely the "done, but not the thing" case, and it is the only step that sees it.

## Escalation — only on an UNEXPLAINED gap

A NOT COVERED bullet with a documented reason (the card's own out-of-scope section, or the
owner descoping it in the thread) is a note, not an escalation. A NOT COVERED bullet the
card plainly asked for, with no such reason, is a real finding. This skill surfaces the
gap; it does not act on it and it never calls the reserve itself — the caller decides
between fix-now, report to the owner, and escalate.

## What the output looks like

```
NOT COVERED  README "What it installs" — parity test names it; numstat shows README.md untouched
COVERED      template exists at templates/petbox-card-check/SKILL.md
COVERED      PROJECT_SKILLS row added — numstat: 2 1 src/.../skill-files.ts
EXTRA        removed the stale .gitignore line for the old name (not asked; in scope)
FOR: no — the kit still fails its own parity gate, so nothing ships to a project yet.
```
