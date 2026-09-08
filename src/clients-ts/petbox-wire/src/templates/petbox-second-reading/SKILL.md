---
name: petbox-second-reading
description: >-
  Use before spawning a worker on an ask that names an alternative or a range ("or", "either",
  "however much is reasonable", "at least"), the moment you notice you already picked one path
  among several, or when the owner calls it directly. A blind second reading of deliverables and
  open choices, checked against your own sealed reading before any plan or diff exists.
petbox: managed
petbox-digest: manual
---

# Second reading — a blind check on the ask before the work, walked again at acceptance

## The disease

Process discipline holds: instructions followed, gates green, the card reads clean. What narrows
is content, silently, at the moment the ask gets *read* — "do whatever is reasonable" becomes one
option, "pick one of these" becomes the cheapest, and nothing records that a choice was made.
Nothing trips on it, because nothing broke. A stronger review of the *output* can't catch this —
it's a faithful build of the narrowed reading, not a bug in it. Only a second, independent reading
of the same ask — compared against the first before either becomes work — catches it.

## The one rule everything else depends on

**The second reader never sees your reading.** Never pass your plan, diff, code, branch name,
hypotheses, or preferred option — each anchors the second head on your own narrowing. The only
legitimate input is the ask as it currently exists. Step 2 closes the other half: anchoring isn't
only what you say, it's how you hand it over.

## Step 1 — seal your own reading

Answer what the reader will get, plus one line more: every deliverable; every open choice; one
sentence of what it's for. Write it in your OWN reply, before calling the reader — never a card
comment, never `session_append`, never a plan file. Anywhere the reader's tools could reach it
voids the check; say so rather than report it passed. Written after the reader returns, it's
hindsight, not a seal.

## Step 2 — the blind reader

Input is the OWNER's words, verbatim: the message or comment where they asked, plus every reply
**by the owner** since, in full. A card or list YOU wrote is your own reading, not the ask — if
the only card is yours, hand over the owner's original words instead, never the card; a card the
owner wrote themselves can stand as the ask. Cut everything that isn't the owner's — your own
comments, a worker's report, a prior acceptance checklist — and say so ("N non-owner comments
removed"). No card needed at all.

Anchoring runs through the channel too, not just content: an opener, which thread slice you
attach, a stray file path, bold on your favorite option, a leading third question — each anchors
like your plan would. Close that off with a fixed spawn:

```
Below is a request, verbatim. Do not look at the repo, boards or memory.
1. List every deliverable it asks for.
2. Quote every sentence that names more than one way, a default, or a condition
   ("or", "either", "whichever", "if reasonable", "ideally", "at least").
3. Quote the sentence that says why it is wanted, or answer "none".
Do not propose an implementation, do not rank options.
--- REQUEST ---
<pasted whole, unformatted>
```

The cheap tier (`explore`; opencode's cheap role) only catches choices the text marks; an
unmarked one is step 4's job, not this reader's.

## Step 3 — diff the two lists

Four outcomes, each a finding, none a dispute:

- **Match** — proceed.
- **Reader has a deliverable you don't** — find it in the owner's words and fold it in, or write
  the cut on the card where the owner sees it, before you spawn. Going quiet is the defect.
- **You have a deliverable the reader doesn't** — find it in the owner's words or drop it; the ask
  is the arbiter, not you.
- **Reader quoted an open choice you'd already closed** — ask the owner one question, or write
  down which way you're going, on the card, as your own call, before you spawn. Recording your
  choice doesn't make it the owner's — it stays your reading, just an honest one.

Disagreement here is the procedure working, not a failure of it.

Once step 3 is settled, post the union — the reader's deliverables plus yours, with every
discrepancy and how it resolved — as a comment on the card, under the fixed heading `Second
reading — acceptance checklist`, never folded into the ask's own body, so a later pass can cut it
by that heading. There is nothing left to seal: both readings are already compared. Step 5 then
walks that comment, not memory. No card exists → `session_append` under the same heading; a chat
message holding the ask cannot hold the checklist too.

## Step 4 — reserve, on event only

Only on: a substantive mismatch on an expensive or hard-to-reverse fork, or you and the reader
disagreeing about *what the ask is for*. Input is the ask plus BOTH readings — never a plan, never
a diff. Verdict, fixed form:

```
SCOPE: matches | narrower (missing: …) | wider (extra: …)
OPEN CHOICES: <choice> — needs owner | any is fine
FOR (in the owner's words): <one sentence>
DISPOSITION: go | ask owner | rewrite the ask
```

`ask owner` is a stop you may not lift yourself.

## Step 5 — acceptance tail (a later, separate moment)

Steps 1-4 run before any code exists; this runs after — a different moment, not a continuation.
Walk the union posted under `Second reading — acceptance checklist` — the reader's list and your
sealed one, with every discrepancy and how it resolved — never your own plan alone, and never
from memory even if the session never compacted. One word per bullet: COVERED / NOT COVERED
(partial counts as NOT COVERED) / EXTRA. `git diff --numstat <base>...HEAD -- <paths>`,
three-dot from the merge-base — `--numstat`, never `--stat` (abbreviates paths; commit
`0080c198`). Live exit code, never a claim in chat. A diff command that errors is a finding, not
"no diff."

## What this skill does not do

Not a gate, not a status, never automatic — called by hand. It does not hunt bugs, judge code
quality, or weigh style: content only. It surfaces a mismatch and stops; which visible action to
take is the caller's call, not this skill's.

## What the output looks like

```
STEP 3: reader has a deliverable you don't — "README update" is in the owner's words, absent
from your sealed reading.
ACTION: folded the README bullet into the card before spawning.
POSTED: acceptance checklist on the card.
```
