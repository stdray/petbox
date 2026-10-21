## Briefing a reviewer or the oracle — [own]

`reviewer` answers checks; `oracle` answers judgement calls. Both are spent on work that already
exists — a brief that says "audit this" and nothing else buys a slow walk over the whole machine
and a list of maybe-findings.

- **A brief is a check, not an investigation.** Give claims, not topics: one bullet per claim, and
  what counts as an answer — the place that decides it, the command whose output decides it.
  "Read these two files and tell me whether X holds", not "verify everything about Y".
- **The brief sets the questions and the evidence boundary, not an exhaustive file list.**
  Reading a dependency the claim leads to, and non-mutating diagnostic commands, are part of the
  check and are allowed inside the stated budget.
- **State a budget and mean it**: tool calls, words, whether experiments are allowed. "Answer from
  the code, run nothing" is a legitimate brief and often the right one. Reaching the budget means
  reporting an unresolved gap, not inventing a verdict. A brief that forbids execution narrows
  `amd-verification` for that pass: answer from the evidence given, and say what would have to be
  run to decide it.
- **Research, broad investigation, state-changing experiments, or a budget increase need the
  owner's approval** — before launch, or before widening. That is the line between checking work
  and paying for exploration. A diagnostic is inside the budget when it is aimed at a claim in the
  brief and changes nothing; the same command aimed at "is there anything else" is broad
  investigation. A command that changes the target system's state, installs software, or
  starts/restarts a service is a state-changing experiment; launching a process solely to perform
  a non-mutating diagnostic is not.
- **Intervene by steering, never by killing.** If a running reviewer or oracle is going the wrong
  way, or is spending more than it should, send it a message that narrows the question; if it is
  already finished, resume it. Do not stop it and start a fresh one: one or two turns already
  spent cost less than the whole run thrown away, and a discarded run is pure loss.
- **Close the pass when the items are answered.** Adding "and also check…" to a running reviewer
  is a second brief.
- **Escalate instead of widening.** A reviewer that cannot decide from the given evidence answers
  "not determinable, needs X". That closes the item: the parent carries it as an open gap in its
  own report and does not widen the brief on its own. If the owner is unreachable, the change
  waits — it is not published, deployed or committed on the strength of an unanswered item.
- **For publication briefs specifically**, see `amd-publication`: the draft and its evidence go to
  the reviewer before anything leaves the machine.

Not to be confused with `amd-reviewer-brief`: that one is about the *content* of work you send back
for review (what context the reviewer needs, what to ask about); this file is about the *shape of
the brief* you give a reviewer or the oracle.
