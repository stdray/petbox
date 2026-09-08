---
name: petbox-second-reading
description: >-
  Use before spawning a worker on work that is expensive or hard to reverse, when the ask itself
  admits more than one honest reading. Never automatic — the caller decides when both conditions
  hold, before any code or plan exists.
petbox: managed
petbox-digest: manual
---

# Second reading — a blind check on the ask, before the work exists

## The disease

Process discipline holds all the way through: instructions get followed, gates run green, the
card reads clean. What narrows is content, silently, at the moment the ask gets *read* — a "do
whatever is reasonable" turns into one option, a "pick one of these" turns into the cheapest one,
and nothing anywhere records that a choice was even made. Nothing trips on it, because nothing
broke: the report at the end is formally complete. A stronger review of the *output* cannot catch
this — the output is a faithful build of the narrowed reading, not a mistake inside it. The only
thing that catches a narrowed reading is a second, independent reading of the same ask, compared
against the first before either one becomes work.

## The one rule everything else depends on

**The second reader never sees your reading.** Never pass it: your plan, your diff, your code,
your branch name, your hypotheses, your preferred option. Handing over any of these anchors the
second head on your own narrowing and defeats the entire point of asking — "just enough context to
help them understand" IS the leak; there is no safe amount. The only legitimate input is the ask
itself, as it currently exists.

## Step 1 — seal your own reading

Before spawning anyone, write 3-5 lines: what closes it / what is out of scope / what it is for,
one sentence. Do this *before* the spawn, and never show it to the reader — it is a record for
step 3's comparison, not a work plan and not a briefing.

## Step 2 — the blind reader

Spawn the cheapest tier that can read (`explore`; the cheap role tier on the opencode arm). Give
it exactly one thing: **the ask as it currently exists** — the card verbatim plus the thread where
it was clarified, if a card exists; otherwise the owner's own words, verbatim. Do not require a
card first — demanding one before this check runs is the same formalism the owner already
rejected, and it would silently exempt the most common case: the owner sitting down and calling
this by hand with no card at all.

Ask exactly two questions, nothing else:
1. List every deliverable the ask asks for.
2. List every open choice the ask leaves — an "either", a default, anything readable two ways.

The reader is explicitly forbidden from proposing an implementation or judging difficulty — this
is extraction, not judgment, which is why the cheap tier is enough.

## Step 3 — diff the two lists

Three outcomes, each a finding, none a dispute:

- **Match** — proceed.
- **Reader named a deliverable you don't have** — you narrowed the scope.
- **Reader named an open choice you closed silently** — that is a question for the owner, not
  something either head gets to decide alone.

Disagreement here is the procedure working, not a failure of it.

## Step 4 — reserve, on event only

Fires only on: a substantive mismatch on an expensive or hard-to-reverse fork, or the two of you
disagreeing about *what the ask is for*. Input to the reserve is the ask plus BOTH readings — never
a plan, never a diff (a plan anchors; a diff is already acceptance). Verdict, fixed form:

```
SCOPE: matches | narrower (missing: …) | wider (extra: …)
OPEN CHOICES: <choice> — needs owner | any is fine
FOR (in the owner's words): <one sentence>
DISPOSITION: go | ask owner | rewrite the ask
```

The caller posts this as a comment, on the reserve's behalf — the reserve itself never writes
nodes. `ask owner` is a stop the caller may not lift; `go` is the absence of an objection, not an
approval.

## Step 5 — acceptance tail

Short, and scored against the **ask**, never against your own plan — that substitution is exactly
the defect this skill replaces. `git diff --numstat <base>...HEAD -- <paths>`: **`--numstat` only,
never `--stat`** (it abbreviates a long path to `.../`, so a real gap reads COVERED — commit
`0080c198`). Three-dot range from the merge-base. Live exit code, never a claim in chat. One word
per bullet: COVERED / NOT COVERED (partial counts as NOT COVERED) / EXTRA. A failure of the diff
command itself is a finding, not "no diff to report".

## What this skill does not do

Not a gate, not a status, never automatic — called by hand. It does not hunt bugs, judge code
quality, or weigh style: content only. It surfaces a mismatch and stops there; fix-now,
ask-the-owner, or proceed-anyway is the caller's call, not this skill's.

## What the output looks like

```
STEP 3: reader named a deliverable you don't have — "README update" is in the ask, absent from
your sealed reading.
DISPOSITION: no reserve needed (cheap fork) — go, fold the README bullet in before spawning.
```
