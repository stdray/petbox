# Agent Interaction Audit — Playbook

A recurring, autonomous audit of how coding agents (Claude Code, opencode, Factory
Droid — the wired triad) actually interact with PetBox, run against the **live**
`$system` project. It catches drift between the agent-facing surfaces and reality,
stale plan state, uncommitted work, and process violations in the session archive.

**Spec:** `agent-interaction-audit` (spec board). **Cadence:** weekly. The first run
is done by hand as calibration; thereafter a scheduled session runs this file.

## What this audit is (and is not)

- It is a **read + report** routine. Its ceiling is filing findings — it creates
  `intake` issues and writes a short owner report. It does **not** fix surfaces,
  close zombie cards, prune worktrees, or edit docs. Remediation is separate work the
  owner triages from the intake cards (that keeps the audit cheap, safe to schedule,
  and honest — the auditor never grades its own homework).
- It audits the **interaction surface and process adherence**, not product
  correctness. Bugs in features are a different flow (intake bug → spec).

## Preconditions

1. A checkout of this repo on `main`, reasonably fresh (`git fetch` first). The
   surface-freshness check compares committed files against live state, so run it
   against `main`, not a feature branch.
2. `PETBOX_API_KEY` set to a cross-project `$system` key (memory:read, tasks:read/
   write, are the minimum — write is only for filing intake cards).
3. The `petbox` MCP server reachable (`whoami` succeeds).

Search before you re-derive: much of what the audit needs is already in memory and
the session archive. Use `memory_search` / `session_search` rather than re-deriving
project history.

---

## Check A — surface freshness

The agent-facing surfaces must describe the **current** system. A surface that lies
is worse than a missing one: a strong model silently prefers the live source and the
rot stays invisible (that is exactly how AGENTS.md rot survived — see memory
`ac-6dda3c…` / the agent-memory-symmetry evidence), while a weaker model (opencode +
deepseek) takes the stale surface as truth and breaks.

**Surfaces in scope:**
- `AGENTS.md` (the shared contract; the primary surface for opencode/droid).
- `.claude/skills/*/SKILL.md` and `agents/wiring/templates/SKILL.md`.
- MCP tool **descriptions** as served live (the text an agent reads to decide how to
  call a tool) vs. what the tool actually accepts/returns.
- The wiring kit's injected canon / friction-duty text (`agents/wiring/protocol.ts`,
  `canon.ts`) vs. the live canon (`GET /api/memory/$system/canon`).

**Procedure:**
1. Read each surface. For every **checkable claim** — a count, a name, a path, a flag,
   a process rule — verify it against the live system:
   - Tool count / names ("~72 tools, underscore-named") → `whoami` tool list.
   - Build/target claims → `build.cs`, `.github/workflows/`.
   - Process-contract steps → the methodology (`tasks_methodology_get`) and the canon
     index (`memory_get $system canon index`).
   - Module/entity claims → the actual project layout.
2. For tool descriptions: pick the tools an agent leans on most (`tasks_*`, `memory_*`,
   `session_*`) and confirm the description matches the live schema — parameters,
   required fields, gotchas. Renames are the usual rot source (schema goes stale
   in-session after a rename; smoke new params from a **fresh** session).
3. Flag any **contradiction** between a surface and reality, and any **silent
   omission** an agent would trip on (a rule enforced in practice but undocumented,
   or documented but no longer enforced).

**Finding =** a specific stale/contradictory line, with the surface path, the claim,
and the live truth. (Not "AGENTS.md feels old" — cite the line.)

## Check B — zombie cards

A zombie is an **open** card whose work is already delivered — the fix is committed/
merged/shipped but the card still sits in Pending/Review, or a spec item shows
`in_progress` while its only task is effectively done. Zombies poison the "living
truth" the boards are supposed to be.

**Procedure:**
1. List open work: `tasks_search board:work status:[Pending,Review]` (+ ideas/spec in
   non-terminal states).
2. For each, look for evidence it is actually done:
   - `commitRef` set, or a commit whose message/scope names the card slug —
     `git log --oneline --all --grep <slug>`.
   - The described change already present on `main` (read the touched file).
   - Shipped: the commit is at/behind the `deploy` tag and live `/version`.
3. A card that is delivered but still open (especially **Review** with a merged
   commit, or Pending whose change is already on `main`) is a zombie → finding.
   Also flag the inverse: a card marked done/closed whose change is **not** in the
   code (a false-done).

**Finding =** card slug + board + status + the commit/line proving it's delivered (or
proving it isn't), and the suggested correct status.

## Check C — uncommitted tails & stale worktrees

Finished edits must be committed on a feature branch and pushed before a card reaches
Review (process contract §4). Uncommitted work in a checkout is invisible, unbacked,
and a merge hazard.

**Procedure:**
1. `git worktree list` — enumerate every checkout.
2. For each worktree: `git -C <path> status --porcelain` (uncommitted/untracked
   changes) and `git -C <path> log --oneline origin/<branch>..HEAD` (committed but
   unpushed commits).
3. Classify:
   - **Uncommitted tail** — dirty working tree in any checkout → finding (highest
     signal in the primary/main checkout).
   - **Unpushed commits** — local commits not on the remote → finding.
   - **Stale worktree** — a worktree whose branch is already merged to `main` (its
     tip is an ancestor of `main`, or the branch no longer exists on the remote) and
     that has no dirty state. This is cleanup debt, not a process breach — report as
     a batch (count + list), not one finding per worktree.
4. **Merged branches travel with the worktree.** A stale worktree almost always has a
   stale branch behind it — the cleanup unit is `git worktree remove <path>` **and**
   `git branch -d <branch>` (and `git push origin --delete <branch>` if it was pushed
   and is merged). Enumerate branches too: `git branch --merged main` (local) and
   `git branch -r --merged origin/main` (remote) minus `main`/`deploy` are deletable.
   A merged branch with **no** worktree is still cleanup debt — fold it into the same
   batched finding. Never delete an **unmerged** branch (it may hold unmerged work).

**Finding =** for tails/unpushed: worktree path + branch + the dirty/unpushed detail.
For stale worktrees/branches: a single batched finding listing each worktree with its
branch and merged-status, plus any merged branches that have no worktree.

## Check D — session-archive sampling (process violations)

Sample recent sessions and check whether the agent followed the process contract.
This is where **false-verify** lives — an agent that *claimed* it verified without
actually doing so.

**Procedure:**
1. `session_search` (or list) for the last week's sessions; pick a sample (all of a
   light week; ~5–8 of a heavy one), biasing toward non-CC agents and toward sessions
   that touched code.
2. For each sampled session, reconstruct the **call trace** (`session_get` on the
   blob; opencode/droid traces can also be pulled from their on-disk stores — see the
   agent-memory-symmetry evidence comments for the extraction recipe) and check it
   against the contract:
   - **Code before card** — edits with no preceding intake/work card.
   - **No worktree** — edits made directly in the primary checkout.
   - **Self-graded status** — agent set `Done`/`accepted` itself (ceiling is Review).
   - **Uncommitted finish** — work called finished / moved to Review with no
     branch+commit+push and no `commitRef`.
   - **Deploy without command**, or a deploy with no post-deploy live smoke.
   - **Silent workaround** — hit a process/doc defect and worked around it instead of
     filing an intake card.
   - **false-verify (the priority class)** — the agent *asserted* a result it never
     established. Method: for every claim of the form "проверил / verified / tests
     pass / build green / it works", find the backing tool call in the trace. No
     backing call, or a call whose output contradicts the claim, = false-verify.
     Tells: "тесты прошли" with no test run and no `.tmp/test-run.log`; "проверил
     X" with no read of X; a green claim right after an error with no re-run. (The
     canonical case: opencode + deepseek in experiment B claimed verification without
     reading — that is the exact class this check exists to catch.)

**Finding =** session id + agent + the specific violation + the message ordinal / quote
that evidences it. For false-verify, cite both the claim and the missing/contradicting
tool call.

> Verify-before-asserting applies to **this audit too**: every finding must cite its
> own evidence (a line, a commit, a trace ordinal). A finding you can't back is
> itself a false-verify — drop it.

---

## Output

Two artifacts each run:

### 1. Intake cards

One `intake` issue per **actionable** finding (batch the pure-cleanup ones, e.g.
stale worktrees, into a single card). Use `tasks_upsert`:

```
board: intake   type: issue   status: reported
title: "audit: <one-line finding>"   (the "audit:" title prefix is how a run's
                                       findings are grouped — tag namespaces can't)
body:  <what, where, the evidence (path/commit/session ordinal), suggested fix>
tags:  ["concern:process"]   (only the area|concern namespaces exist; a custom
                              "audit:" namespace is rejected — group via the title)
```

Do **not** promote, fix, or self-triage — `reported` is the ceiling; the owner
triages. If an identical finding is already an open intake card, comment on it rather
than filing a duplicate (search first).

### 2. Owner report

A short comment on the `interaction-audit-routine` work card (or the run's session):

```
## Interaction audit — <date>
- Scope: surfaces audited, N sessions sampled, M worktrees scanned.
- Findings: <count by class>, each linking its intake card.
- Clean: what was checked and found healthy (so a clean area is on record).
- Calibration notes (first runs): checks that were noisy / low-signal / need tuning.
```

Keep it to what the owner needs to decide next; link permalinks, not slugs.

## Calibration (first runs)

The first few runs are calibration, not steady state. Expect noise — especially a
backlog of long-stale worktrees and old sessions predating the current contract.
Judgement calls for the first run:
- Sample **recent** sessions only; don't retro-audit the whole archive against a
  contract that didn't exist yet.
- Batch the historical worktree backlog into one finding rather than dozens.
- Note in the report which checks were low-signal, so the cadence/scope can be tuned
  before automation is trusted to run unattended.
