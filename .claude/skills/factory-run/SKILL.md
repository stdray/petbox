---
name: factory-run
description: >-
  Run a batch of already-written task statements to completion in one unattended pass —
  fan out one implementer per task into its own worktree, merge sequentially, gate the
  combined tree, deploy, verify on the live stand, close the cards. Use when several
  prepared, mostly independent tasks must all land (a folder of session briefs, a set of
  accepted work cards, "прогони всё это"), especially when the owner will be away and the
  orchestrator must keep going alone. Not for a single task, for exploratory work, or for
  tasks that mostly touch the same files — see "When not to use this".
---

# Factory run — many prepared tasks, one unattended pass

The orchestrator's job is decisions and seams. Implementation, investigation and
verification go to subagents; the orchestrator holds the merge order, the gates, the
deploy and the card statuses, and nothing else. Reading a subagent's transcript defeats
the whole arrangement — a run of this shape moves millions of tokens through subagents
while the orchestrator spends a fraction of that.

Every rule below is written because breaking it cost a real run time. Where a rule names
a consequence, that consequence was observed, not imagined.

## The pipeline

Per task, in order. The orchestrator owns steps 5–10 and never delegates them.

1. **Recon (read-only, before anything is decided).** A few agents read the statements
   *and* the linked cards/specs, and report: real scope, files touched, overlap with the
   other tasks in the batch, and — the point of the exercise — **material questions for
   the owner**, each with options and a recommendation.
2. **One batch of questions to the owner.** Every material fork at once, with a
   recommendation for each, so the answer can be "all as recommended" plus exceptions.
   This is what makes unattended work possible; questions discovered later stall a task
   until the owner returns.
3. **Implementer per task**, in its own worktree branched from `origin/main`.
4. **Agent pushes its branch** and moves its card to Review. Never further.
5. **Orchestrator merges sequentially** into the primary checkout.
6. **Combined gate** on the merged tree.
7. **Push `main`, then move the `deploy` tag** (see the repo's AGENTS.md; the tag run is
   the whole pipeline).
8. **Verifier agents on the live stand**, with the concrete steps taken from the
   implementers' reports.
9. **Disposition every tail from the implementers' reports — the run does not end with an
   open list.** Four outcomes, one per item: fix now (a few lines, in files this run already
   touched); discard, with a one-line reason in the run report; one line to memory; escalate
   as a self-contained card the owner can act on without this run's context. A tail is
   escalated only if leaving it unfixed risks losing data, letting an unauthorized party in,
   or breaking something a user can see — and the risk is live now, not hypothetical. Check
   "still open?" first: many tails close during the run itself. When unsure, discard — a real
   problem returns on its own; a hypothetical one returns only as the owner's reading load.
10. **Orchestrator moves cards to Done** — only for what the live stand actually confirmed.

## Rules that cost something to learn

**The orchestrator merges; agents never do.** Parallel merges into a shared primary
checkout race against each other. Sequential merging by one hand costs about half a
minute per branch and deletes an entire class of failure. Tell agents explicitly: push
the branch, do not merge, do not touch the primary checkout.

**The combined gate is a step, not a precaution.** Branches that are green alone break
together. In one run an agent changed a public signature while another agent's new E2E
test called the old one — both branches honestly green, because each merged `origin/main`
at a different moment and neither could see the other. Only the merged tree shows it.

**"Gate running in the background, will report" reports nothing.** A backgrounded process
dies with the agent's turn. Require the gate in the foreground, finished inside the same
turn, and require the *literal* summary lines in the report, not a paraphrase. Raise the
call timeout rather than moving the run to the background. Related trap: a PowerShell
build script invoked through bash fails with a shell syntax error and **exits 0** — it
looks like a passing gate.

**A report naming a destination is not proof of delivery.** Five implementers out of six
wrote a "TAILS → collector card" section and wrote nothing to the card. One of them
explained it exactly: *"I did not lose the write to CAS — I never made it. The report had
a section with the destination and I took the heading for the act."* Verify any claimed
write by reading the target. One call; the alternative is findings that exist only in a
dead transcript.

**Card bodies lose concurrent writes; comments don't.** A shared collector edited by many
agents drops writes silently on CAS. Where agents must write to a shared card at all, have
them post comments, never edit the body.

**Demand red-proof, not green-proof.** "The test passes" is worth nothing. "The test
fails when I remove the fix" is evidence. The strongest work in a run came from agents
that mutated their own change and showed the failure output — one of them injected a SQL
trigger to abort between two inserts, and added a control test so a green result could
not mean "failed for another reason".

**Hand the agent the owner's decision, not the dilemma** — and include what was rejected
and why. Agents given the reasoning pushed back correctly when the decision rested on a
false premise; agents given a bare instruction would have implemented it.

**Ask for disagreement in the prompt.** In one run three implementers overturned the
brief and were right every time: one proved the owner could not have the account state
the brief assumed, one showed the classification unit in the brief would silently
misclassify seven unrelated types, one showed a count in the brief was miscopied. Put it
in writing: if the premise is wrong, say so and stop rather than comply.

**Forbid fixing adjacent defects — tails live in the report, not in a card.** Each
implementer lists incidental findings in a `TAILS` section of its report, one line each:
what and where. Otherwise diffs bloat and review gets harder exactly where the real change
needs attention. No shared collector card: a batch-wide accumulator outlives its run and
lands on the owner's desk as a page of unsorted "defects" — three of them piled up in eight
days, one still waiting on the owner a day later. The orchestrator dispositions every tail
at the end of the run (step 9); nothing survives as an open list.

**Hardcoded counters are guaranteed merge conflicts.** Ratchet tests that pin a number
(surface counts, tool counts) collide whenever two parallel branches each add one. Decide
up front which branch owns the counter, or derive it.

**Agents die; their worktrees usually survive.** An auth blip killed seven agents at
once. Before relaunching, run `git status` in the agent's worktree — work is typically
intact and uncommitted, so the right move is resume-with-context, not restart. Tell the
resumed agent what you found on disk so it does not redo it.

**Watch for the agent that stops without doing anything.** One returned after its
self-introduction with zero tool calls. Only per-report review catches this.

**Shared-checkout tools race with the gate.** Anything that rebuilds the primary checkout
(a static-analysis model refresh, another agent's build) while the gate or a push runs
produces a *false* red gate and can block the push outright — a pre-push inspection tool
exits non-zero because another build holds the directory. Before believing a red gate,
check whether something else was building.

**Check real exit codes.** A backgrounded command whose output is piped reports the exit
code of the last command in the pipe. A failed push was reported as success this way.

## The implementer prompt

Reused verbatim per task; only the task-specific block changes.

- **Statement + source of truth.** Path to the brief, and the explicit note that the
  brief is secondary — the card and spec on the board win.
- **The owner's decision** for this task's fork, with rejected options and why.
- **The pipeline steps**: fetch, worktree strictly from `origin/main`, implement, tests,
  foreground gate, push branch, card to Review. `Done` is the owner's gate, never the
  agent's.
- **Named traps** already known for this task, from recon.
- **Tails**: do not fix adjacent defects; list them in a `TAILS` section of your report, one
  line each — what and where. An empty section is a good result, not a gap; never pad.
- **Report shape**: branch and sha; what was done; what each test catches and how it was
  shown to fail without the fix; the literal gate output; risks to the live stand; the
  concrete steps to verify on the live stand after deploy.

Put the riskiest thing first in the requested report shape ("blocking risks to the live
stand, first item, even if there are none") — it surfaces in the notification preview
instead of being buried.

## Verification on the live stand

**Verify the opposite direction too.** A marker present on every record distinguishes
nothing; a refusal whose wording differs between "absent" and "forbidden" is an
enumeration oracle. Ask for both halves explicitly — "confirm the tag is present on
background records **and** absent on user records".

**Never write into a real project.** Sandbox project, sandbox-only keys, clean up
afterwards, and say so in the report.

**Some tasks are unverifiable live, and that is a finding, not a failure.** Data-only
changes, test-only changes and anything needing a config edit plus a restart cannot be
confirmed on a running production stand. Make the verifier say which half the live stand
covered and which half only CI covers — and prove the unverifiability (one run traced it
to a singleton reading `IOptions` once at construction, so no hot-reload path exists)
rather than shrugging.

**Hold Done when the central claim has no live evidence.** Breadth gaps are acceptable —
a mechanism confirmed on four of thirteen services is confirmed. A claim with zero live
evidence is not, however green its tests.

## When not to use this

- **A single task**, or two. The pipeline's overhead only pays off across a batch.
- **Exploratory work** where the goal is understanding, not landing changes — use
  `analysis-workspace` instead.
- **Tasks that mostly touch the same files.** Recon should establish independence first;
  heavy overlap means the batch should be a sequence, not a fan-out.
- **No prepared statements.** Writing the briefs is its own job; this skill starts where
  they already exist.
