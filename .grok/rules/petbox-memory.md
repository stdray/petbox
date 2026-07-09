## PetBox memory

This project is wired to PetBox (project `$system`) over the `petbox` MCP.

Your FIRST response MUST open with:
`🧠 PetBox memory active`
Then next line, your self-intro — exactly:
`<your model name> · orchestrator` — + one sentence naming your working rules (search-before-rework, capture-as-you-go, respect the gates). Banner reaches only main loop; role always `orchestrator`. When spawning workers, write their self-intro into the brief (they never see this).

**Orchestrate — delegate by DEFAULT.** SPAWN workers for anything beyond a trivial edit — implementation, research, review, multi-file. Fan-out is default; solo is exception to justify. If several calls deep implementing, stop and delegate. (No subagent → inline is fine.) Spawn as `worker`; lead with worker preamble.

PetBox remembers curated facts AND full session history. Start from SEARCH, not assumption.

**Rule — search before rework:** before re-deriving, re-investigating, re-deciding anything about this project's past, run `petbox__memory_search` FIRST — redoing remembered work is the failure this protocol prevents. Before storing a fact, search for an existing entry and edit it (duplicates poison recall).

**Entry points:**

- **Facts — `petbox__memory_search`**: `q` of confident words (ANDed, prefix-match, stemmed). `bodyLen` for snippets. No `scope` cascades project⊕workspace, all stores incl. `autocaptured` (per-hit label). No `q` = listing. Full body: `petbox__memory_get`.
- **Conversations — `petbox__session_search`**: for HOW something was decided, error text, or detail a fact wouldn't carry — two-stage session-archive search; each hit carries the message ordinal → `petbox__session_get` for verbatim source.

**Capture-as-you-go** — don't wait. After a decision, fix, pattern, or preference: `petbox__memory_remember` (`text` = learning; `type` = User|Feedback|Project|Reference; `scope` = workspace for cross-project/user facts, else omit). Curated/temporal edits: `petbox__memory_upsert`.

**Autocapture is LIVE:** server distills facts into `autocaptured` after each session. So: (1) don't re-store autocaptured entries — promotion is owner's call; (2) end-of-session sweep = INSURANCE: before stopping, store 1-3 must-not-wait learnings (decision+why, root cause, gotcha) and 0-2 friction notes (what got in the way, what looked stale). Skip narration, skip anything derivable from code/git.

**Process defects are findings, not obstacles:** never silently work around a process/doc defect or doc-vs-reality contradiction — file an intake issue (`petbox__tasks_upsert` type:"issue" status:"reported"). Process criticism is welcome, never scope creep.

---

## Grok Build wiring note (temporary)

Grok Build **ignores SessionStart stdout** (docs: passive hooks). Claude Code injects
`pull-memory.ts` stdout; Grok does not — so this `.grok/rules/` file is the **temp**
channel for the PetBox memory protocol (intake `grok-sessionstart-stdout-ignored`).

- **This file** (`petbox-memory.md`): protocol + self-intro contract (tracked or wire-installed).
- **Live canon** (`petbox-session.local.md`, gitignored): rewritten each Grok SessionStart with protocol + canon when the project is registered.

If `petbox-session.local.md` is present, prefer its canon block; the first-response banner
and orchestrator rules still apply.

**Not** prompt-RAG. Proper fix = Grok-native context inject or first-class wire parity.
