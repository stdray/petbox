---
name: comprehension-check
description: >-
  Sverka (comprehension check) of finished work against a task card, aimed at "done not
  fully" rather than "done not the right thing". Use right after reading a delegated task
  card, before touching code, to write down what it commits to and what it excludes — and
  again at the end, before reporting Review or before an orchestrator accepts a worker's
  result, to walk the diff against those points and name anything left uncovered. Not an
  automatic gate: nothing calls this by itself, the worker or orchestrator invokes it on
  purpose. Two of its four steps are pure mechanical checks that need no model judgment at
  all — do the artifacts the card named exist, did the named test go green, does the diff
  touch what the card said it would.
---

# Comprehension check — sverka postanovki

A weak-model worker and a stronger-model orchestrator already hold process discipline well
(instructions, MCP calls, methodology gates). What both miss, quietly, is whether the
*content* of the result matches the *content* of the ask — a task finished cleanly but
covering less than the card asked for, with no error anywhere to trip on. This skill is a
procedure against that gap, not a smarter model: every step below is something a plain
worker can execute by reading and running commands.

**Honest limit, stated up front so it is never oversold:** run on the same model that did
the task, this procedure is good at catching *"not fully done"* — a bullet from the card
simply absent from the diff. It is weak at catching *"not the right thing"* — busy work
that touches every literal bullet but misses the point. Aim at the first; do not claim the
second.

## When to call this

- **ENTRY** — right after reading the task card, before writing any code.
- **EXIT** — before reporting a task as Review/done (worker), or before accepting a
  worker's result (orchestrator).

Nothing triggers this automatically. No board status, no hook. The caller — a worker
following its brief, or an orchestrator at acceptance — decides to run it, same as any
other skill. Do not wire it to a status or a schedule; that turns a cheap explicit check
into a methodology change, which is out of scope here (see the source card's "Что
отвергнуто").

## Step 0 — the card is checkable at all (mechanical)

A card can only be checked against if it says, in its own words, what "done" and "not
done" mean. Grep the card body for a definition-of-done section ("Чем закрывается" /
"Definition of done" / "Closes with") and an out-of-scope section ("Что НЕ входит" / "Out
of scope" / "Not included"). Either missing is itself a finding — flag it back to whoever
wrote the card rather than guessing at the boundary; guessing is exactly the failure mode
this skill exists to catch.

## Step 1 — ENTRY: extract the commitments (one short pass, before coding)

From the card body, before starting work, write down:

- **3-5 bullets of "what will be done"** — concrete and checkable, in the card's own
  language. Do not paraphrase into something broader or narrower than what the card says;
  that paraphrase is where a comprehension gap is silently introduced.
- **the "what will not be done"** — the card's out-of-scope section, close to verbatim.

Keep this list. It is the only input Step 3 needs, and writing it before coding means a
misreading is visible immediately, for the cost of one short pass, instead of after the
work is done.

## Step 2 — EXIT: mechanical checks (no model judgment — run commands, read exit codes)

Three checks, each a command, each a yes/no fact, not an opinion:

1. **Do the artifacts the card names exist** at the paths/names the card used (files,
   skills, scripts, config keys)?
2. **Did the test/gate the card names actually run, and is it green** — the real exit
   code, never a claim in a chat message?
3. **Does the diff touch what the card named?** `git diff --stat <base>...HEAD` against
   what the card said would be touched — anything named but not touched is a live
   candidate for "not fully done"; anything touched but not named is either fine or scope
   creep, check it against the card's out-of-scope section.

`scripts/mechanical_check.sh` runs (1) and (3) for you — see its `--help`. Do not replace
these three with a model call "reading" the same facts; that only adds cost and a new
place to be wrong about something a command answers exactly.

## Step 3 — EXIT: point-by-point against the diff (this is the model's job)

Walk the Step 1 list against the real diff/files. For each bullet, answer with exactly one
of three words:

- **COVERED**
- **NOT COVERED**
- **EXTRA** (done beyond what the card asked)

Do not soften a partial into "mostly covered" — partial is NOT COVERED. Output the list
flat, one line per bullet, so it can be read at a glance. If any bullet is NOT COVERED,
that is the headline of the report, named verbatim from the card — not folded into a
generic "looks good" summary.

## Step 4 — escalate only on a real, unexplained gap

This skill never calls the reserve model itself. A NOT COVERED bullet that the worker can
point to a documented reason for (the card's own out-of-scope section says so, or the
owner descoped it in the thread) is not an escalation — note it and move on. A NOT
COVERED bullet the card plainly asked for, with no such reason, is a real finding: the
caller (worker or orchestrator) decides whether that means fix-now, report to the owner,
or escalate to reserve — this skill only surfaces the gap, it does not act on it.

## Self-test

`self-test/` holds a fixture task card and an intentionally incomplete "result" for it —
one named artifact missing outright, one named function never added inside a file that
does exist. Run `scripts/mechanical_check.sh` against the fixture to see Step 2 name the
missing artifact without any model call; `self-test/README.md` walks through the Step 3
model pass on the same fixture and shows it naming the second, content-level gap that a
mechanical check cannot see. Re-run either when changing the script.
